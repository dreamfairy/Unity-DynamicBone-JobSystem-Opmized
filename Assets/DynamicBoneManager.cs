using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Jobs;
using Unity.Mathematics;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;

public class DynamicBoneManager : MonoBehaviour
{
    private static DynamicBoneManager m_instance;

    public static DynamicBoneManager Instance
    {
        get
        {
            if(null == m_instance)
            {
                m_instance = GameObject.FindObjectOfType<DynamicBoneManager>();
                if (!m_instance)
                {
                    GameObject go = new GameObject("DynamicBoneManager");
                    m_instance = go.AddComponent<DynamicBoneManager>();
                }
                m_instance.Init();
            }

            return m_instance;
        }
    }

    [BurstCompile]
    struct RootPosApplyJob : IJobParallelForTransform
    {
        public NativeArray<DynamicBoneBeta.HeadInfo> ParticleHeadInfo;

        public void Execute(int index, TransformAccess transform)
        {
            DynamicBoneBeta.HeadInfo headInfo = ParticleHeadInfo[index];
            headInfo.m_RootParentBoneWorldPos = transform.position;
            headInfo.m_RootParentBoneWorldRot = transform.rotation;

            ParticleHeadInfo[index] = headInfo;
        }
    }

    [BurstCompile]
    struct PrepareParticleJob : IJob
    {
        [ReadOnly]
        public NativeArray<DynamicBoneBeta.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneBeta.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute()
        {
            for (int i = 0; i < HeadCount; i++)
            {
                DynamicBoneBeta.HeadInfo curHeadInfo = ParticleHeadInfo[i];

                float3 parentPosition = curHeadInfo.m_RootParentBoneWorldPos;
                quaternion parentRotation = curHeadInfo.m_RootParentBoneWorldRot;

                for (int j = 0; j < curHeadInfo.m_particleCount; j++)
                {
                    int pIdx = curHeadInfo.m_jobDataOffset + j;
                    DynamicBoneBeta.Particle p = ParticleInfo[pIdx];

                    var localPosition = p.localPosition * p.parentScale;
                    var localRotation = p.localRotation;
                    var worldPosition = parentPosition + math.mul(parentRotation, localPosition);
                    var worldRotation = math.mul(parentRotation, localRotation);

                    p.worldPosition = worldPosition;
                    p.worldRotation = worldRotation;

                    parentPosition = worldPosition;
                    parentRotation = worldRotation;

                    ParticleInfo[pIdx] = p;
                }
            }
        }
    }

    [BurstCompile]
    struct UpdateParticles1Job : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneBeta.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneBeta.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute(int index)
        {
            {
                int headIndex = index / DynamicBoneBeta.MAX_TRANSFORM_LIMIT;
                DynamicBoneBeta.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];


                {
                    int singleId = index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT;
                  
                    if (singleId >= curHeadInfo.m_particleCount) return;

                    int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT);

                    DynamicBoneBeta.Particle p = ParticleInfo[pIdx];

                    if (p.m_ParentIndex >= 0)
                    {
                        float3 ev = p.tmpWorldPosition - p.tmpPrevWorldPosition;
                        float3 evrmove = curHeadInfo.m_ObjectMove * p.m_Inert;
                        p.tmpPrevWorldPosition = p.tmpWorldPosition + evrmove;

                        float edamping = p.m_Damping;
                        if (p.m_isCollide == 1)
                        {
                            edamping += p.m_Friction;
                            if (edamping > 1)
                                edamping = 1;
                            p.m_isCollide = 0;
                        }

                        float3 eForce = curHeadInfo.m_PerFrameForce;
                        float3 tmp = ev * (1 - edamping) + eForce + evrmove;
                        p.tmpWorldPosition += tmp;
                    }
                    else
                    {
                        p.tmpPrevWorldPosition = p.tmpWorldPosition;
                        p.tmpWorldPosition = p.worldPosition;
                    }

                    ParticleInfo[pIdx] = p;
                }
            }
        }
    }

    [BurstCompile]
    struct UpdateParticle2Job : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneBeta.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneBeta.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute(int index)
        {
            {
                if (index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT == 0) return;

                int headIndex = index / DynamicBoneBeta.MAX_TRANSFORM_LIMIT;
                DynamicBoneBeta.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];
                {
                    int singleId = index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT;

                    if (singleId >= curHeadInfo.m_particleCount) return;

                    int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT);

                    DynamicBoneBeta.Particle p = ParticleInfo[pIdx];
                    int p0Idx = curHeadInfo.m_jobDataOffset + p.m_ParentIndex;
                    DynamicBoneBeta.Particle p0 = ParticleInfo[p0Idx];

                    float3 ePos = p.worldPosition;
                    float3 ep0Pos = p0.worldPosition;

                    float erestLen = math.distance(ep0Pos, ePos);

                    float stiffness = Mathf.Lerp(1.0f, p.m_Stiffness, curHeadInfo.m_Weight);
                    if (stiffness > 0 || p.m_Elasticity > 0)
                    {
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
                    if (eleng > 0)
                    {
                        float3 tmp = edd * ((eleng - erestLen) / eleng);
                        p.tmpWorldPosition += tmp;
                    }

                    ParticleInfo[pIdx] = p;
                }
            }
        }
    }

    [BurstCompile]
    struct ApplyParticleToTransform : IJobParallelFor
    {
        [ReadOnly]
        public NativeArray<DynamicBoneBeta.HeadInfo> ParticleHeadInfo;
        public NativeArray<DynamicBoneBeta.Particle> ParticleInfo;
        public int HeadCount;

        public void Execute(int index)
        {
            {
                if (index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT == 0)
                {
                    return;
                }

                int headIndex = index / DynamicBoneBeta.MAX_TRANSFORM_LIMIT;

                DynamicBoneBeta.HeadInfo curHeadInfo = ParticleHeadInfo[headIndex];
                {
                    int singleId = index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT;

                    if (singleId >= curHeadInfo.m_particleCount) return;

                    int pIdx = curHeadInfo.m_jobDataOffset + (index % DynamicBoneBeta.MAX_TRANSFORM_LIMIT);

                    DynamicBoneBeta.Particle p = ParticleInfo[pIdx];
                    int p0Idx = curHeadInfo.m_jobDataOffset + p.m_ParentIndex;
                    DynamicBoneBeta.Particle p0 = ParticleInfo[p0Idx];

                    if (p0.m_ChildCount <= 1)
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

                    ParticleInfo[pIdx] = p;
                    ParticleInfo[p0Idx] = p0;
                }
            }
        }
    }

    [BurstCompile]
    struct FinalJob : IJobParallelForTransform
    {
        [ReadOnly]
        public NativeArray<DynamicBoneBeta.Particle> ParticleInfo;

        public void Execute(int index, TransformAccess transform)
        {
            transform.rotation = ParticleInfo[index].worldRotation;
            transform.position = ParticleInfo[index].worldPosition;
        }
    }


    private List<DynamicBoneBeta> m_dynamicBoneList;
    private NativeList<DynamicBoneBeta.Particle> m_particleInfo;
    private NativeList<DynamicBoneBeta.HeadInfo> m_headInfo;


    private TransformAccessArray m_headRootTransform;
    private TransformAccessArray m_particleTransformArr;
    private int m_DbDataLen = 0;
    private JobHandle m_lastJobHandle;


    private void Awake()
    {
        if (!m_instance)
        {
            m_instance = this;
            m_instance.Init();
        }
    }

    public void Init()
     {
        m_dynamicBoneList = new List<DynamicBoneBeta>();
        m_particleInfo = new NativeList<DynamicBoneBeta.Particle>(Allocator.Persistent);
        m_headInfo = new NativeList<DynamicBoneBeta.HeadInfo>(Allocator.Persistent);
        m_particleTransformArr = new TransformAccessArray(200 * DynamicBoneBeta.MAX_TRANSFORM_LIMIT, 64);
        m_headRootTransform = new TransformAccessArray(200, 64);
    }

    private Queue<DynamicBoneBeta> m_loadingQueue = new Queue<DynamicBoneBeta>();
    private Queue<DynamicBoneBeta> m_removeQueue = new Queue<DynamicBoneBeta>();

    void UpdateQueue()
    {
        while(m_loadingQueue.Count > 0)
        {
            DynamicBoneBeta target = m_loadingQueue.Dequeue();

            int idx = m_dynamicBoneList.IndexOf(target);
            if (idx == -1)
            {
                m_dynamicBoneList.Add(target);



                target.m_headInfo.m_jobDataOffset = m_particleInfo.Length;

                int headIndex = m_headInfo.Length;
                target.m_headInfo.ResetHeadIndex(headIndex);

                m_headInfo.Add(target.m_headInfo);
                m_particleInfo.AddRange(target.m_Particles);
                m_headRootTransform.Add(target.m_rootParentTransform);

                for (int i = 0; i < DynamicBoneBeta.MAX_TRANSFORM_LIMIT; i++)
                {
                    m_particleTransformArr.Add(target.m_particleTransformArr[i]);
                }

                m_DbDataLen++;
            }
        }

        while(m_removeQueue.Count > 0)
        {
            DynamicBoneBeta target = m_removeQueue.Dequeue();

            int idx = m_dynamicBoneList.IndexOf(target);
            if (idx != -1)
            {
                m_dynamicBoneList.RemoveAt(idx);

                int curHeadIndex = target.m_headInfo.GetHeadIndex();

                //是否是队列中末尾对象
                bool isEndTarget = curHeadIndex == m_headInfo.Length - 1;
                if (isEndTarget)
                {
                    m_headInfo.RemoveAtSwapBack(curHeadIndex);
                    m_headRootTransform.RemoveAtSwapBack(curHeadIndex);

                    for (int i = DynamicBoneBeta.MAX_TRANSFORM_LIMIT - 1; i >= 0; i--)
                    {
                        int dataOffset = curHeadIndex * DynamicBoneBeta.MAX_TRANSFORM_LIMIT + i;
                        m_particleInfo.RemoveAtSwapBack(dataOffset);
                        m_particleTransformArr.RemoveAtSwapBack(dataOffset);
                    }
                }
                else
                {
                    //将最末列的HeadInfo 索引设置为当前将要移除的HeadInfo 索引
                    DynamicBoneBeta lastTarget = m_dynamicBoneList[m_dynamicBoneList.Count - 1];

                    DynamicBoneBeta.HeadInfo lastHeadInfo = lastTarget.ResetHeadIndexAndDataOffset(curHeadIndex);

                    m_headInfo.RemoveAtSwapBack(curHeadIndex);

                    m_headInfo[curHeadIndex] = lastHeadInfo;

                    m_headRootTransform.RemoveAtSwapBack(curHeadIndex);

                    for (int i = DynamicBoneBeta.MAX_TRANSFORM_LIMIT - 1; i >= 0; i--)
                    {
                        int dataOffset = curHeadIndex * DynamicBoneBeta.MAX_TRANSFORM_LIMIT + i;
                        m_particleInfo.RemoveAtSwapBack(dataOffset);
                        m_particleTransformArr.RemoveAtSwapBack(dataOffset);
                    }
                }

                m_DbDataLen--;
            }

            target.ClearJobData();
        }
    }

    public void OnEnter(DynamicBoneBeta target, ref DynamicBoneBeta.HeadInfo headInfo, NativeArray<DynamicBoneBeta.Particle> particleInfo, Transform[] particleTransformList)
    {
        m_loadingQueue.Enqueue(target);
    }

    public void OnExit(DynamicBoneBeta target, ref DynamicBoneBeta.HeadInfo headInfo)
    {
        m_removeQueue.Enqueue(target);
    }

    private void Update()
    {
        if (m_DbDataLen == 0)
        {
            return;
        }
    }

    private void LateUpdate()
    {
        if (!m_lastJobHandle.IsCompleted)
        {
            return;
        }

        m_lastJobHandle.Complete();

        UpdateQueue();

        if (m_DbDataLen == 0)
        {
            return;
        }

        var dataArrLength = m_DbDataLen * DynamicBoneBeta.MAX_TRANSFORM_LIMIT;

        var rootJob = new RootPosApplyJob
        {
            ParticleHeadInfo = this.m_headInfo
        };
        var rootHandle = rootJob.Schedule(m_headRootTransform);

        var prepareJob = new PrepareParticleJob
        {
            ParticleHeadInfo = this.m_headInfo,
            ParticleInfo = this.m_particleInfo,
            HeadCount = m_DbDataLen
        };
        var prepareHandle = prepareJob.Schedule(rootHandle);

        var update1Job = new UpdateParticles1Job
        {
            ParticleHeadInfo = this.m_headInfo,
            ParticleInfo = this.m_particleInfo,
            HeadCount = m_DbDataLen
        };
        var update1Handle = update1Job.Schedule(dataArrLength, DynamicBoneBeta.MAX_TRANSFORM_LIMIT, prepareHandle);

        var update2Job = new UpdateParticle2Job
        {
            ParticleHeadInfo = this.m_headInfo,
            ParticleInfo = this.m_particleInfo,
            HeadCount = m_DbDataLen
        };
        var update2Handle = update2Job.Schedule(dataArrLength, DynamicBoneBeta.MAX_TRANSFORM_LIMIT, update1Handle);

        var appTransJob = new ApplyParticleToTransform
        {
            ParticleHeadInfo = this.m_headInfo,
            ParticleInfo = this.m_particleInfo,
            HeadCount = m_DbDataLen
        };

        var appTransHandle = appTransJob.Schedule(dataArrLength, DynamicBoneBeta.MAX_TRANSFORM_LIMIT, update2Handle);
        var finalJob = new FinalJob
        {
            ParticleInfo = this.m_particleInfo,
        };
        var finalHandle = finalJob.Schedule(this.m_particleTransformArr, appTransHandle);

        m_lastJobHandle = finalHandle;

        JobHandle.ScheduleBatchedJobs();
    }

    private void OnDestroy()
    {
        if (this.m_particleTransformArr.isCreated)
        {
            this.m_particleTransformArr.Dispose();
        }

        if (this.m_particleInfo.IsCreated)
        {
            this.m_particleInfo.Dispose();
        }

        if (this.m_headInfo.IsCreated)
        {
            this.m_headInfo.Dispose();
        }

        if (this.m_headRootTransform.isCreated)
        {
            this.m_headRootTransform.Dispose();
        }
    }
}
