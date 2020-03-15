/**
 * @file            
 * @author          
 * @copyright       
 * @created         2020-02-13 14:46:20
 * @updated         2020-02-13 14:46:20
 *
 * @brief           
 */
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class Respawn : MonoBehaviour
{
    public int Count;
    public int Cur;
    public float IntervalDis;
    public GameObject Target;
    public GameObject SecTarget;

    private GameObject SelectTarget;
    // Start is called before the first frame update
    void Start()
    {
        float3 dual = Vector3.zero;
    }

    IEnumerator CreateTarget()
    {
        int perLineCount = (int)Mathf.Sqrt(Count);

        for(int i = 0; i < Count; i++)
        {
            Cur++;

            float curX = i % perLineCount;
            float curZ = i / perLineCount;

            GameObject go = GameObject.Instantiate(SelectTarget);
            go.transform.parent = this.transform;
            go.transform.localPosition = new Vector3(curX * IntervalDis, 0, curZ * IntervalDis);
            go.SetActive(true);

            yield return new WaitForEndOfFrame();
        }
    }

    private void OnGUI()
    {
        if(GUI.Button(new Rect(0,0,200,200), "SwitchFirst"))
        {
            SelectTarget = Target;
            StartCoroutine(CreateTarget());
        }

        if (GUI.Button(new Rect(0, 250, 200, 200), "SwitchSec"))
        {
            SelectTarget = SecTarget;
            StartCoroutine(CreateTarget());
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
