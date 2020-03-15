using UnityEngine;
using System.Collections;

public class DynamicBoneDemo1 : MonoBehaviour
{
    public GameObject m_Player;
    float m_weight = 1;

    void Update()
    {
        m_Player.transform.Rotate(new Vector3(0, Input.GetAxis("Horizontal") * Time.deltaTime * 200, 0));
        m_Player.transform.Translate(transform.forward * Input.GetAxis("Vertical") * Time.deltaTime * 4);
    }

    void OnGUI()
    {
        GUI.Label(new Rect(50, 50, 200, 24), "Press arrow key to move");
        Animation a = m_Player.GetComponentInChildren<Animation>();
        a.enabled = GUI.Toggle(new Rect(50, 70, 200, 24), a.enabled, "Play Animation");

        DynamicBone[] dbs = m_Player.GetComponents<DynamicBone>();
        GUI.Label(new Rect(50, 100, 200, 24), "Choose dynamic bone:");
        dbs[0].enabled = dbs[1].enabled = GUI.Toggle(new Rect(50, 120, 100, 24), dbs[0].enabled, "Breasts");
        dbs[2].enabled = GUI.Toggle(new Rect(50, 140, 100, 24), dbs[2].enabled, "Tail");

        GUI.Label(new Rect(50, 160, 200, 24), "Weight");
        m_weight = GUI.HorizontalSlider(new Rect(100, 160, 100, 24), m_weight, 0, 1);
        foreach (var db in dbs)
            db.SetWeight(m_weight);
    }
}
