using System.Collections.Generic;
using UnityEngine;

public class CameraFadeObstacles : MonoBehaviour
{
    public Transform target;          // Your player
    public LayerMask obstacleMask;    // Layer for walls/obstacles
    public float fadedAlpha = 0.3f;   // Opacity when blocking
    public float fadeSpeed = 8f;      // How fast it fades in and out

    private HashSet<Renderer> currentlyBlocking = new HashSet<Renderer>();
    private List<Renderer> trackedRenderers = new List<Renderer>();

    void Update()
    {
        currentlyBlocking.Clear();

        Vector3 direction = target.position - transform.position;
        float distance = Vector3.Distance(transform.position, target.position);

        // 1. Find everything currently between the camera and player
        RaycastHit[] hits = Physics.RaycastAll(transform.position, direction, distance, obstacleMask);

        foreach (RaycastHit hit in hits)
        {
            Renderer rend = hit.collider.GetComponent<Renderer>();
            if (rend != null)
            {
                currentlyBlocking.Add(rend);
                if (!trackedRenderers.Contains(rend))
                {
                    trackedRenderers.Add(rend);
                }
            }
        }

        // 2. Fade blocking objects down, and restore non-blocking objects back to 1.0
        for (int i = trackedRenderers.Count - 1; i >= 0; i--)
        {
            Renderer rend = trackedRenderers[i];
            if (rend == null)
            {
                trackedRenderers.RemoveAt(i);
                continue;
            }

            bool isBlocking = currentlyBlocking.Contains(rend);
            float targetAlpha = isBlocking ? fadedAlpha : 1.0f;

            Material mat = rend.material;
            Color color = mat.color;
            color.a = Mathf.MoveTowards(color.a, targetAlpha, fadeSpeed * Time.deltaTime);
            mat.color = color;

            // Stop tracking once it is fully solid again
            if (!isBlocking && Mathf.Approximately(color.a, 1.0f))
            {
                trackedRenderers.RemoveAt(i);
            }
        }
    }
}
