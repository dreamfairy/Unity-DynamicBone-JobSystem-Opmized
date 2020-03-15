using UnityEngine;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Collections;

[AddComponentMenu("Dynamic Bone/Dynamic BoneBeta")]
public class DynamicBoneBeta : MonoBehaviour
{
    public bool outputBySIMD = false;

    public const int MAX_TRANSFORM_LIMIT = 10;

    [Tooltip("The root of the transform hierarchy to apply physics.")]
    public Transform m_Root = null;

    [Tooltip("Internal physics simulation rate.")]
    public float m_UpdateRate = 60.0f;

    public enum UpdateMode
    {
        Normal,
        AnimatePhysics,
        UnscaledTime
    }
    public UpdateMode m_UpdateMode = UpdateMode.Normal;

    [Tooltip("How much the bones slowed down.")]
    [Range(0, 1)]
    public float m_Damping = 0.1f;
    public AnimationCurve m_DampingDistrib = null;

    [Tooltip("How much the force applied to return each bone to original orientation.")]
    [Range(0, 1)]
    public float m_Elasticity = 0.1f;
    public AnimationCurve m_ElasticityDistrib = null;

    [Tooltip("How much bone's original orientation are preserved.")]
    [Range(0, 1)]
    public float m_Stiffness = 0.1f;
    public AnimationCurve m_StiffnessDistrib = null;

    [Tooltip("How much character's position change is ignored in physics simulation.")]
    [Range(0, 1)]
    public float m_Inert = 0;
    public AnimationCurve m_InertDistrib = null;

    [Tooltip("How much the bones slowed down when collide.")]
    public float m_Friction = 0;
    public AnimationCurve m_FrictionDistrib = null;

    [Tooltip("Each bone can be a sphere to collide with colliders. Radius describe sphere's size.")]
    public float m_Radius = 0;
    public AnimationCurve m_RadiusDistrib = null;

    [Tooltip("If End Length is not zero, an extra bone is generated at the end of transform hierarchy.")]
    public float m_EndLength = 0;

    [Tooltip("If End Offset is not zero, an extra bone is generated at the end of transform hierarchy.")]
    public Vector3 m_EndOffset = Vector3.zero;

    [Tooltip("The force apply to bones. Partial force apply to character's initial pose is cancelled out.")]
    public Vector3 m_Gravity = Vector3.zero;

    [Tooltip("The force apply to bones.")]
    public Vector3 m_Force = Vector3.zero;

    [Tooltip("Collider objects interact with the bones.")]
    public List<DynamicBoneColliderBase> m_Colliders = null;

    [Tooltip("Bones exclude from physics simulation.")]
    public List<Transform> m_Exclusions = null;


    public enum FreezeAxis
    {
        None, X, Y, Z
    }
    [Tooltip("Constrain bones to move on specified plane.")]
    public FreezeAxis m_FreezeAxis = FreezeAxis.None;

    [Tooltip("Disable physics simulation automatically if character is far from camera or player.")]	
    public bool m_DistantDisable = false;
    public Transform m_ReferenceObject = null;
    public float m_DistanceToObject = 20;

    public Vector3 m_LocalGravity = Vector3.zero;
    public Vector3 m_ObjectMove = Vector3.zero;
    public Vector3 m_ObjectPrevPosition = Vector3.zero;
    public float m_BoneTotalLength = 0;
    public float m_ObjectScale = 1.0f;
    public float m_Time = 0;
    public float m_Weight = 1.0f;
    public bool m_DistantDisabled = false;

    private Vector3 m_GravityNormalize;

    public struct HeadInfo
    {
        int m_HeadIndex;

        public float m_UpdateRate;
        public Vector3 m_PerFrameForce;

        public Vector3 m_ObjectMove;
        public float m_Weight;
        public int m_particleCount;
        public int m_jobDataOffset;
        public int m_ParticleLoopCount;

        public float3 m_RootParentBoneWorldPos;
        public quaternion m_RootParentBoneWorldRot;

        public void ResetHeadIndex(int index)
        {
            this.m_HeadIndex = index;
        }

        public int GetHeadIndex()
        {
            return this.m_HeadIndex;
        }
    }

    public struct Particle
    {
        public int index;
        public int m_ParentIndex;
        public float m_Damping;
        public float m_Elasticity;
        public float m_Stiffness;
        public float m_Inert;
        public float m_Friction;
        public float m_Radius;
        public float m_BoneLength;
        public int m_isCollide;

        public float3 m_EndOffset;
        public float3 m_InitLocalPosition;
        public quaternion m_InitLocalRotation;

        public int m_ChildCount;


        //for calc worldPos
        public float3 localPosition;
        public quaternion localRotation;

        public float3 tmpWorldPosition;
        public float3 tmpPrevWorldPosition;

        public float3 parentScale;
        public int isRootParticle;

        //for output
        public float3 worldPosition;
        public quaternion worldRotation;
    }

    public NativeArray<Particle> m_Particles;
    public Transform[] m_particleTransformArr;
    private int m_ParticleCount;
    private Transform m_transform;
    public Transform m_rootParentTransform;
    public HeadInfo m_headInfo;

    private void Awake()
    {
        m_transform = this.transform;

        m_headInfo = new HeadInfo();
        m_headInfo.m_UpdateRate = this.m_UpdateRate;
        m_headInfo.m_ObjectMove = this.m_ObjectMove;
        m_headInfo.m_Weight = this.m_Weight;
        m_headInfo.m_particleCount = 0;

        m_Particles = new NativeArray<Particle>(MAX_TRANSFORM_LIMIT, Allocator.Persistent);
        m_particleTransformArr = new Transform[MAX_TRANSFORM_LIMIT];
        m_ParticleCount = 0;

        m_GravityNormalize = m_Gravity.normalized;

        SetupParticles(ref m_headInfo);
      
    }

    public HeadInfo ResetHeadIndexAndDataOffset(int headIndex)
    {
        m_headInfo.ResetHeadIndex(headIndex);
        m_headInfo.m_jobDataOffset = headIndex * MAX_TRANSFORM_LIMIT;

        return m_headInfo;
    }

    void FixedUpdate()
    {
        if (m_UpdateMode == UpdateMode.AnimatePhysics)
            PreUpdate();
    }

    private bool useJob = false;
    void Update()
    {
        if (useJob)
        {
            return;
        }

        if (m_UpdateMode != UpdateMode.AnimatePhysics)
            PreUpdate();
    }

    public void DebugUpdate()
    {
        PreUpdate();
    }


    void PrepareParticle()
    {
        m_headInfo.m_RootParentBoneWorldPos = m_rootParentTransform.position;
        m_headInfo.m_RootParentBoneWorldRot = m_rootParentTransform.rotation;

        float3 parentPosition = m_rootParentTransform.position;
        quaternion parentRotation = m_rootParentTransform.rotation;

        for (int i = 0; i < m_ParticleCount; i++)
        {
            Particle p = m_Particles[i];
            Transform trans = m_particleTransformArr[p.index];

            p.m_ChildCount = trans.childCount;

            var localPosition = p.localPosition * p.parentScale;
            var localRotation = p.localRotation;
            var worldPosition = parentPosition + math.mul(parentRotation, localPosition);
            var worldRotation = math.mul(parentRotation, localRotation);

            p.worldPosition = worldPosition;
            p.worldRotation = worldRotation;

            parentPosition = worldPosition;
            parentRotation = worldRotation;

            m_Particles[i] = p;
        }
    }

    public void PrepareJobData()
    {
        m_headInfo.m_RootParentBoneWorldPos = m_rootParentTransform.position;
        m_headInfo.m_RootParentBoneWorldRot = m_rootParentTransform.rotation;
        m_headInfo.m_ObjectMove = m_transform.position - m_ObjectPrevPosition;

        m_ObjectScale = Mathf.Abs(transform.lossyScale.x);
        m_ObjectPrevPosition = transform.position;

        Vector3 force = m_Gravity;
        Vector3 fdir = m_GravityNormalize;
        Vector3 rf = m_Root.TransformDirection(m_LocalGravity);
        Vector3 pf = fdir * Mathf.Max(Vector3.Dot(rf, fdir), 0);	// project current gravity to rest gravity
        force -= pf;	// remove projected gravity
        force = (force + m_Force) * m_ObjectScale;

        m_headInfo.m_PerFrameForce = force;
    }

    void LateUpdate()
    {
        if (useJob)
        {
            return;
        }

        //if (m_DistantDisable)
        //    CheckDistance();

        if (m_Weight > 0 && !(m_DistantDisable && m_DistantDisabled))
        {
            PrepareParticle();
            float dt = m_UpdateMode == UpdateMode.UnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            UpdateDynamicBones(dt, ref m_headInfo);
        }
    }

    void PreUpdate()
    {
        if (m_Weight > 0 && !(m_DistantDisable && m_DistantDisabled))
            InitTransforms();
    }

    void CheckDistance()
    {
        Transform rt = m_ReferenceObject;
        if (rt == null && Camera.main != null)
            rt = Camera.main.transform;
        if (rt != null)
        {
            float d = (rt.position - transform.position).sqrMagnitude;
            bool disable = d > m_DistanceToObject * m_DistanceToObject;
            if (disable != m_DistantDisabled)
            {
                if (!disable)
                    ResetParticlesPosition(ref m_headInfo);
                m_DistantDisabled = disable;
            }
        }
    }

    void OnEnable()
    {
        //ResetParticlesPosition(ref m_headInfo);

        DynamicBoneManager.Instance.OnEnter(this, ref m_headInfo, this.m_Particles, this.m_particleTransformArr);
        useJob = true;
    }

    void OnDisable()
    {
        InitTransforms();

        DynamicBoneManager.Instance.OnExit(this, ref m_headInfo);
    }

    public void ClearJobData()
    {
        if (m_Particles.IsCreated)
        {
            m_Particles.Dispose();
        }

        m_particleTransformArr = null;
    }

    public void SetWeight(float w, ref HeadInfo head)
    {
        if (head.m_Weight != w)
        {
            if (w == 0)
                InitTransforms();
            else if (head.m_Weight == 0)
                ResetParticlesPosition(ref head);
            head.m_Weight = w;
        }
    }

    public float GetWeight()
    {
        return m_headInfo.m_Weight;
    }

    void UpdateDynamicBones(float t, ref HeadInfo head)
    {
        if (m_Root == null)
            return;
        m_ObjectScale = Mathf.Abs(transform.lossyScale.x);
        m_ObjectMove = m_transform.position - m_ObjectPrevPosition;
        m_ObjectPrevPosition = transform.position;

        Vector3 force = m_Gravity;
        Vector3 fdir = m_Gravity.normalized;
        Vector3 rf = m_Root.TransformDirection(m_LocalGravity);
        Vector3 pf = fdir * Mathf.Max(Vector3.Dot(rf, fdir), 0);	// project current gravity to rest gravity
        force -= pf;	// remove projected gravity
        force = (force + m_Force) * m_ObjectScale;

        int loop = 1;
        if (m_UpdateRate > 0)
        {
            float dt = 1.0f / m_UpdateRate;
            m_Time += t;
            loop = 0;

            while (m_Time >= dt)
            {
                m_Time -= dt;
                if (++loop >= 3)
                {
                    m_Time = 0;
                    break;
                }
            }
        }

        loop = math.max(1, loop);
        if (loop > 0)
        {
            for (int i = 0; i < loop; ++i)
            {
                UpdateParticles1(force, ref head);
                UpdateParticles2(ref head);
                m_ObjectMove = Vector3.zero;
            }
        }

        ApplyParticlesToTransforms();
    }

    public void SetupParticles(ref HeadInfo head)
    {
        //m_Particles.Clear();
        if (m_Root == null)
            return;

        m_rootParentTransform = m_Root.parent;

        m_LocalGravity = m_Root.InverseTransformDirection(m_Gravity);
        m_ObjectScale = Mathf.Abs(transform.lossyScale.x);
        m_ObjectPrevPosition = transform.position;
        m_ObjectMove = Vector3.zero;
        m_BoneTotalLength = 0;
        AppendParticles(m_Root, -1, 0, ref head);
        UpdateParameters();

        for(int i = 0; i < m_ParticleCount; i++)
        {
            m_particleTransformArr[i].parent = null;
        }

        m_headInfo.m_particleCount = m_ParticleCount;
    }

    void AppendParticles(Transform b, int parentIndex, float boneLength, ref HeadInfo head)
    {
        Particle p = new Particle();
        p.index = m_ParticleCount++;
        p.m_ParentIndex = parentIndex;

        if (b != null)
        {
            p.m_InitLocalPosition = b.localPosition;
            p.m_InitLocalRotation = b.localRotation;

            //extend

            p.localPosition = b.localPosition;
            p.localRotation = b.localRotation;
            p.tmpWorldPosition = p.tmpPrevWorldPosition = b.position;

            p.worldPosition = b.position;
            p.worldRotation = b.rotation;

            p.parentScale = b.parent.lossyScale;
            p.isRootParticle = parentIndex == -1 ? 1 : 0;
        }
        else 	// end bone
        {
            Transform pb = m_particleTransformArr[parentIndex];
            if (m_EndLength > 0)
            {
                Transform ppb = pb.parent;
                if (ppb != null)
                    p.m_EndOffset = pb.InverseTransformPoint((pb.position * 2 - ppb.position)) * m_EndLength;
                else
                    p.m_EndOffset = new Vector3(m_EndLength, 0, 0);
            }
            else
            {
                p.m_EndOffset = pb.InverseTransformPoint(transform.TransformDirection(m_EndOffset) + pb.position);
            }
            //p.m_Position = p.m_PrevPosition = pb.TransformPoint(p.m_EndOffset);
            p.tmpWorldPosition = p.tmpPrevWorldPosition = pb.TransformPoint(p.m_EndOffset);
        }

        if (parentIndex >= 0)
        {
            float dis = math.distance(m_particleTransformArr[parentIndex].position, p.tmpWorldPosition);
            boneLength += dis;
            p.m_BoneLength = boneLength;
            m_BoneTotalLength = Mathf.Max(m_BoneTotalLength, boneLength);
        }

        m_Particles[p.index] = p;
        m_particleTransformArr[p.index] = b;

        int index = p.index;

        if (b != null)
        {
            for (int i = 0; i < b.childCount; ++i)
            {
                bool exclude = false;
                if (m_Exclusions != null)
                {
                    for (int j = 0; j < m_Exclusions.Count; ++j)
                    {
                        Transform e = m_Exclusions[j];
                        if (e == b.GetChild(i))
                        {
                            exclude = true;
                            break;
                        }
                    }
                }
                if (!exclude)
                    AppendParticles(b.GetChild(i), index, boneLength, ref head);
                else if (m_EndLength > 0 || m_EndOffset != Vector3.zero)
                    AppendParticles(null, index, boneLength, ref head);
            }

            if (b.childCount == 0 && (m_EndLength > 0 || m_EndOffset != Vector3.zero))
                AppendParticles(null, index, boneLength, ref head);
        }
    }

    public void UpdateParameters()
    {
        if (m_Root == null)
            return;

        m_LocalGravity = m_Root.InverseTransformDirection(m_Gravity);

        for (int i = 0; i < m_ParticleCount; ++i)
        {
            Particle p = m_Particles[i];
            p.m_Damping = m_Damping;
            p.m_Elasticity = m_Elasticity;
            p.m_Stiffness = m_Stiffness;
            p.m_Inert = m_Inert;
            p.m_Friction = m_Friction;
            p.m_Radius = m_Radius;

            if (m_BoneTotalLength > 0)
            {
                float a = p.m_BoneLength / m_BoneTotalLength;
                if (m_DampingDistrib != null && m_DampingDistrib.keys.Length > 0)
                    p.m_Damping *= m_DampingDistrib.Evaluate(a);
                if (m_ElasticityDistrib != null && m_ElasticityDistrib.keys.Length > 0)
                    p.m_Elasticity *= m_ElasticityDistrib.Evaluate(a);
                if (m_StiffnessDistrib != null && m_StiffnessDistrib.keys.Length > 0)
                    p.m_Stiffness *= m_StiffnessDistrib.Evaluate(a);
                if (m_InertDistrib != null && m_InertDistrib.keys.Length > 0)
                    p.m_Inert *= m_InertDistrib.Evaluate(a);
                if (m_FrictionDistrib != null && m_FrictionDistrib.keys.Length > 0)
                    p.m_Friction *= m_FrictionDistrib.Evaluate(a);
                if (m_RadiusDistrib != null && m_RadiusDistrib.keys.Length > 0)
                    p.m_Radius *= m_RadiusDistrib.Evaluate(a);
            }

            p.m_Damping = Mathf.Clamp01(p.m_Damping);
            p.m_Elasticity = Mathf.Clamp01(p.m_Elasticity);
            p.m_Stiffness = Mathf.Clamp01(p.m_Stiffness);
            p.m_Inert = Mathf.Clamp01(p.m_Inert);
            p.m_Friction = Mathf.Clamp01(p.m_Friction);
            p.m_Radius = Mathf.Max(p.m_Radius, 0);



            m_Particles[i] = p;
        }
    }

    void InitTransforms()
    {
        for (int i = 0; i < m_ParticleCount; ++i)
        {
            Particle p = m_Particles[i];
            Transform trans = m_particleTransformArr[p.index];
            if (trans != null)
            {
                //trans.localPosition = p.m_InitLocalPosition;
                //trans.localRotation = p.m_InitLocalRotation;
                p.localPosition = p.m_InitLocalPosition;
                p.localRotation = p.m_InitLocalRotation;
            }
        }
    }

    void ResetParticlesPosition(ref HeadInfo head)
    {
        //for (int i = 0; i < m_ParticleCount; ++i)
        //{
        //    Particle p = m_Particles[i];
        //    Transform trans = m_particleTransformArr[p.index];
        //    if (trans != null)
        //    {
        //        p.m_Position = p.m_PrevPosition = trans.position;
        //    }
        //    else	// end bone
        //    {
        //        Transform pb = m_particleTransformArr[p.m_ParentIndex];
        //        p.m_Position = p.m_PrevPosition = pb.TransformPoint(p.m_EndOffset);
        //    }
        //    p.m_isCollide = 0;
        //    m_Particles[i] = p;
        //}
        m_ObjectPrevPosition = m_transform.position;
    }

    void UpdateParticles1(Vector3 force, ref HeadInfo head)
    {
        for (int i = 0; i < m_ParticleCount; ++i)
        {
            Particle p = m_Particles[i];

            if (p.m_ParentIndex >= 0)
            {
                //extend
                float3 ev = p.tmpWorldPosition - p.tmpPrevWorldPosition;
                float3 evrmove = head.m_ObjectMove * p.m_Inert;
                p.tmpPrevWorldPosition = p.tmpWorldPosition + evrmove;

                float edamping = p.m_Damping;
                if (p.m_isCollide == 1)
                {
                    edamping += p.m_Friction;
                    if (edamping > 1)
                        edamping = 1;
                    p.m_isCollide = 0;
                }

                float3 eForce = force;
                float3 tmp = ev * (1 - edamping) + eForce + evrmove;
                p.tmpWorldPosition += tmp;

            }
            else
            {
                //extend
                p.tmpPrevWorldPosition = p.tmpWorldPosition;
                p.tmpWorldPosition = p.worldPosition;
            }

            m_Particles[i] = p;
        }
    }

    void UpdateParticles2(ref HeadInfo head)
    {
        for (int i = 1; i < m_ParticleCount; ++i)
        {
            Particle p = m_Particles[i];
            Particle p0 = m_Particles[p.m_ParentIndex];

            float3 ePos = p.worldPosition;
            float3 ep0Pos = p0.worldPosition;


            float erestLen = math.distance(ep0Pos, ePos);

            // keep shape
            float stiffness = Mathf.Lerp(1.0f, p.m_Stiffness, head.m_Weight);
            if (stiffness > 0 || p.m_Elasticity > 0)
            {
                //extend, 本地坐标变换到父级空间下计算距离
                float4x4 em0 = float4x4.TRS(p0.tmpWorldPosition, p0.worldRotation, p.parentScale);
                float3 erestPos = math.mul(em0, new float4(p.localPosition.xyz, 1)).xyz;
                float3 ed = erestPos - p.tmpWorldPosition;
                float3 eStepElasticity = ed * p.m_Elasticity;
                p.tmpWorldPosition += eStepElasticity;

                if (stiffness > 0)
                {
                    float len = math.distance(erestPos, p.tmpWorldPosition);
                    float maxlen = erestLen * (1 - stiffness) * 2;
                    if (len > maxlen)
                    {
                        float3 max = ed * ((len - maxlen) / len);
                        p.tmpWorldPosition += max;
                    }
                }

            }

            float3 edd = p0.tmpWorldPosition - p.tmpWorldPosition;
            float eleng = math.distance(p0.tmpWorldPosition, p.tmpWorldPosition);
            if(eleng > 0)
            {
                float3 tmp = edd * ((eleng - erestLen) / eleng);
                p.tmpWorldPosition += tmp;
            }

            m_Particles[p.index] = p;
        }
    }

    static Vector3 MirrorVector(Vector3 v, Vector3 axis)
    {
        return v - axis * (Vector3.Dot(v, axis) * 2);
    }

    void ApplyParticlesToTransforms()
    {
        Particle parentP = m_Particles[0];

        for (int i = 1; i < m_ParticleCount; ++i)
        {
            Particle p = m_Particles[i];
            Particle p0 = parentP;

            if (p0.m_ChildCount <= 1)       // do not modify bone orientation if has more then one child
            {
                float3 ev = p.localPosition;
                float3 ev2 = p.tmpWorldPosition - p0.tmpWorldPosition;

                float4x4 epm = float4x4.TRS(p.worldPosition, p.worldRotation, p.parentScale);

                var worldV = math.mul(epm, new float4(ev, 0)).xyz;
                Quaternion erot = Quaternion.FromToRotation(worldV, ev2);
                var eoutputRot = math.mul(erot, p.worldRotation);
                p0.worldRotation = eoutputRot;
            }

            p.worldPosition = p.tmpWorldPosition;

            m_Particles[i] = p;
            m_Particles[p.m_ParentIndex] = p0;

            parentP = p;
        }


        for (int i = 0; i < m_ParticleCount; i++)
        {
            Particle p = m_Particles[i];
            Transform trans = m_particleTransformArr[p.index];

            trans.rotation = p.worldRotation;
            trans.position = p.worldPosition;
        }
    }
}
