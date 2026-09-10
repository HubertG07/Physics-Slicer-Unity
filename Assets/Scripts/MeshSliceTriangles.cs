using System;
using UnityEngine;
using UnityEngine.SocialPlatforms;

public class MeshSliceTriangles : MonoBehaviour
{
    [Header("Plane Config")]
    [SerializeField] private Transform planeTransform;

    [Header("Visualisation Settings")]
    [Tooltip("Radius of drawn gizmo sphere at each vertex")]
    [SerializeField] private float gizmoSphereRadius = 0.05f;

    private MeshFilter targetObjectMeshFilter;
    private Vector3[] localVertices;
    private int[] triangles;

    void Awake()
    {
        CacheComponents();
    }

    void OnDrawGizmos()
    {
        // Ensure components exist before drawing any gizmos 
        if (planeTransform == null) return;

        if (targetObjectMeshFilter == null || localVertices == null || triangles == null)
        {
            // Attempt to grab the mesh filter in case Awake failed
            CacheComponents();
            if (targetObjectMeshFilter == null || targetObjectMeshFilter.sharedMesh == null ) return;
        }

        Vector3 localPlanePosition = transform.InverseTransformPoint(planeTransform.position);
        Vector3 localPlaneNormal = transform.InverseTransformDirection(planeTransform.up).normalized;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            // Fetch position and indices of all 3 vertices of the triangle face
            int indice0 = triangles[i];
            int indice1 = triangles[i + 1];
            int indice2 = triangles[i + 2];

            Vector3 vertex0 = localVertices[indice0];
            Vector3 vertex1 = localVertices[indice1];
            Vector3 vertex2 = localVertices[indice2];

            
            float distance0 = Vector3.Dot((vertex0 - localPlanePosition), localPlaneNormal);
            float distance1 = Vector3.Dot((vertex1 - localPlanePosition), localPlaneNormal);
            float distance2 = Vector3.Dot((vertex2 - localPlanePosition), localPlaneNormal);

            CheckAndDrawEdge(vertex0, vertex1, distance0, distance1);
            CheckAndDrawEdge(vertex1, vertex2, distance1, distance2);
            CheckAndDrawEdge(vertex2, vertex0, distance2, distance0);
        }
    }

    private void CheckAndDrawEdge(Vector3 vertexA, Vector3 vertexB, float distA, float distB)
    {
        if (distA * distB < 0f) // Opposite signs means the edge is cut
        {
            Vector3 localSplitPoint = IntersectEdge(vertexA, vertexB, distA, distB);
            Vector3 worldSplitPoint = transform.TransformPoint(localSplitPoint);

            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(worldSplitPoint, gizmoSphereRadius);
        }
    }

    private Vector3 IntersectEdge(Vector3 vertexA, Vector3 vertexB, float distA, float distB)
    {
        // Follow the equation t = |distA| / (|distA9| + |distB|)
        float t = Mathf.Abs(distA) / (Mathf.Abs(distA) + Mathf.Abs(distB));

        return Vector3.Lerp(vertexA, vertexB, t);
    }

    /// <summary>
    /// Fetches required local components
    /// </summary>
    private void CacheComponents()
    {
        targetObjectMeshFilter = GetComponent<MeshFilter>();
        if (targetObjectMeshFilter != null && targetObjectMeshFilter.sharedMesh != null)
        {
            localVertices = targetObjectMeshFilter.sharedMesh.vertices;
            triangles = targetObjectMeshFilter.sharedMesh.triangles;
        }
    }

}
