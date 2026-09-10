using System.Collections.Generic;
using UnityEditor.EditorTools;
using UnityEngine;

/// <summary>
///  Visualise mesh vertex classification depending on a cutting plane using scene gizmos
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class MeshSliceTest : MonoBehaviour
{
    [Header("Plane Config")]
    [SerializeField] private Transform planeTransform;

    [Header("Visualisation Settings")]
    [Tooltip("Radius of drawn gizmo sphere at each vertex")]
    [SerializeField] private float gizmoSphereRadius = 0.05f;

    private MeshFilter targetObjectMeshFilter;
    private Vector3[] localVertices;

    void Awake()
    {
        CacheComponents();
    }

    void OnDrawGizmos()
    {
        // Ensure components exist before drawing any gizmos 
        if (planeTransform == null) return;

        if (targetObjectMeshFilter == null || localVertices == null || localVertices.Length == 0)
        {
            // Attempt to grab the mesh filter in case Awake failed
            CacheComponents();
            if (targetObjectMeshFilter == null || targetObjectMeshFilter.sharedMesh == null ) return;
        }

        Vector3 localPlanePosition = transform.InverseTransformPoint(planeTransform.position);
        Vector3 localPlaeNormal = transform.InverseTransformDirection(planeTransform.up).normalized;

        // For loop to draw each vertex
        for (int count = 0; count < localVertices.Length; count++)
        {
            float distance = Vector3.Dot((localVertices[count] - localPlanePosition), localPlaeNormal);

            Gizmos.color = (distance >= 0f) ? Color.green : Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(localVertices[count]), gizmoSphereRadius);
        }
    }

    /// <summary>
    /// Fetches required local components
    /// </summary>
    private void CacheComponents()
    {
        targetObjectMeshFilter = GetComponent<MeshFilter>();
        localVertices = targetObjectMeshFilter.sharedMesh.vertices;
    }
}
