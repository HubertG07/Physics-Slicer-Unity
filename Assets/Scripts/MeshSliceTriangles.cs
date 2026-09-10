using System;
using UnityEngine;

/// <summary>
/// Visualise mesh vertex along with triangle intersection points using scene gizmos
/// </summary>
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

    /// <summary>
    /// Checks if an edge intersects the surface boundary
    /// </summary>
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

    /// <summary>
    /// Calculates the point of intersection along the edge of two vertices
    /// </summary>
    /// <param name="vertexA">Position of the first vertex</param>
    /// <param name="vertexB">Position of the second vertex</param>
    /// <param name="distA">Scalar distance at <paramref name="vertexA"/></param>
    /// <param name="distB">Scalar distance at <paramref name="vertexB"/></param>
    /// <returns>Interpolated position where surface intersects the edge</returns>
    private Vector3 IntersectEdge(Vector3 vertexA, Vector3 vertexB, float distA, float distB)
    {
        float absA = Mathf.Abs(distA);
        float absB = Mathf.Abs(distB);
        float denominator = absA + absB;

        // Prevent division by 0 if distance are close to it
        if (denominator < 0.00001f) return vertexA;

        // Follow the equation t = |distA| / (|distA| + |distB|)
        float t = absA / denominator;

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
