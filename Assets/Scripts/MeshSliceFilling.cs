using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Slices and reconstructs a mesh into two seperate game objects along a plane boundary
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class MeshSliceFilling : MonoBehaviour
{
    [SerializeField] private Transform planeTransform;

    private MeshFilter targetMeshFilter;
    private Vector3[] localVertices;
    private int[] triangles;
    private List<Vector3> capVertices = new List<Vector3>();

    private InputAction eAction;

    void Awake()
    {
        CacheComponents();
        eAction = new InputAction(binding: "<Keyboard>/e");
        eAction.performed += OnEPressed;
    }

    private void OnEnable() => eAction.Enable();
    private void OnDisable() => eAction.Disable();

    private void OnDestroy()
    {
        eAction.performed -= OnEPressed;
        eAction.Dispose();
    }

    private void OnEPressed(InputAction.CallbackContext context)
    {
        SliceMesh();
    }

    /// <summary>
    /// Slices the mesh into two distinct meshes relevative to the plane's orientation and position
    /// </summary>
    public void SliceMesh()
    {
        if (planeTransform == null || targetMeshFilter == null) return;

        capVertices.Clear();

        MeshData aboveMesh = new MeshData();
        MeshData belowMesh = new MeshData();

        Vector3 localPlanePosition = transform.InverseTransformPoint(planeTransform.position);
        Vector3 localPlaneNormal = transform.InverseTransformDirection(planeTransform.up).normalized;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            // Fetch position of all 3 vertices of the triangle face
            Vector3 vertex0 = localVertices[triangles[i]];
            Vector3 vertex1 = localVertices[triangles[i + 1]];
            Vector3 vertex2 = localVertices[triangles[i + 2]];

            float dist0 = Vector3.Dot((vertex0 - localPlanePosition), localPlaneNormal);
            float dist1 = Vector3.Dot((vertex1 - localPlanePosition), localPlaneNormal);
            float dist2 = Vector3.Dot((vertex2 - localPlanePosition), localPlaneNormal);

            if (dist0 >= 0 && dist1 >= 0 && dist2 >= 0)
            {
                // Uncut (everything is above)
                AddUncutTriangle(aboveMesh, vertex0, vertex1, vertex2);
            }
            else if (dist0 < 0 && dist1 < 0 && dist2 < 0)
            {
                //Uncut (everything below)
                AddUncutTriangle(belowMesh, vertex0, vertex1, vertex2);
            }
            else
            {
                // Split the triangles across the plane
                SplitTriangle(aboveMesh, belowMesh, vertex0, vertex1, vertex2, dist0, dist1, dist2);
            }
        }

        // Fill the exposed surface boundary
        CapMesh(aboveMesh, belowMesh, localPlaneNormal);

        // Convert above and below mesh into real mesh objects
        GameObject aboveObject = CreateSlicedObject(aboveMesh, $"{gameObject.name}_Above");
        GameObject belowObject = CreateSlicedObject(belowMesh, $"{gameObject.name}_Below");

        gameObject.SetActive(false);
    }

    /// <summary>
    /// Add the unsplit triangle directly to the target mesh
    /// </summary>
    private void AddUncutTriangle(MeshData meshData, Vector3 vertex0, Vector3 vertex1, Vector3 vertex2)
    {
        int indice0 = meshData.AddVertex(vertex0);
        int indice1 = meshData.AddVertex(vertex1);
        int indice2 = meshData.AddVertex(vertex2);
        meshData.AddTriangle(indice0, indice1, indice2);
    }

    /// <summary>
    /// Splits an intersecting triangle into smaller triangles across the plane boundary
    /// </summary>
    private void SplitTriangle(MeshData above, MeshData below,
                            Vector3 vertex0, Vector3 vertex1, Vector3 vertex2,
                            float dist0, float dist1, float dist2)
    {
        Vector3 loneVert, pairVert1, pairVert2;
        float loneDist, pairDist1, pairDist2;

        // Identify which vertex is on its own side of the plane
        if ((dist0 >= 0 && dist1 < 0 && dist2 < 0) || (dist0 < 0 && dist1 >= 0 && dist2 >= 0))
        {
            loneVert = vertex0; loneDist = dist0;
            pairVert1 = vertex1; pairDist1 = dist1;
            pairVert2 = vertex2; pairDist2 = dist2;
        }
        else if ((dist1 >= 0 && dist0 < 0 && dist2 < 0) || (dist1 < 0 && dist0 >= 0 && dist2 >= 0))
        {
            loneVert = vertex1; loneDist = dist1;
            pairVert1 = vertex2; pairDist1 = dist2;
            pairVert2 = vertex0; pairDist2 = dist0;
        }
        else
        {
            loneVert = vertex2; loneDist = dist2;
            pairVert1 = vertex0; pairDist1 = dist0;
            pairVert2 = vertex1; pairDist2 = dist1;
        }

        
        Vector3 cutA = IntersectEdge(loneVert, pairVert1, loneDist, pairDist1);
        Vector3 cutB = IntersectEdge(loneVert, pairVert2, loneDist, pairDist2);

        capVertices.Add(cutA);
        capVertices.Add(cutB);

        MeshData loneSideMesh = (loneDist >= 0) ? above : below;
        MeshData pairSideMesh = (loneDist >= 0) ? below : above;

        AddUncutTriangle(loneSideMesh, loneVert, cutA, cutB);

        int iPair1 = pairSideMesh.AddVertex(pairVert1);
        int iPair2 = pairSideMesh.AddVertex(pairVert2);
        int iCutA = pairSideMesh.AddVertex(cutA);
        int iCutB = pairSideMesh.AddVertex(cutB);

        pairSideMesh.AddTriangle(iPair1, iPair2, iCutA);
        pairSideMesh.AddTriangle(iPair2, iCutB, iCutA);
    }

    /// <summary>
    /// Calculates intersection point along edge of two vertices
    /// </summary>
    /// <param name="vertexA">Position of first vertex</param>
    /// <param name="vertexB">Position of second vertex</param>
    /// <param name="distA">Scalar distance at vertexA</param>
    /// <param name="distB">Scalar distance at vertexB</param>
    /// <returns>Interpolated position where plane intersects edge</returns>
    private Vector3 IntersectEdge(Vector3 a, Vector3 b, float distA, float distB)
    {
        float denom = Mathf.Abs(distA) + Mathf.Abs(distB);
        if (denom < 0.00001f) return a;
        
        float t = Mathf.Abs(distA) / denom;
        return Vector3.Lerp(a, b, t);
    }

    /// <summary>
    /// Fetches required local components
    /// </summary>
    private void CacheComponents()
    {
        targetMeshFilter = GetComponent<MeshFilter>();
        if (targetMeshFilter != null && targetMeshFilter.sharedMesh != null)
        {
            localVertices = targetMeshFilter.sharedMesh.vertices;
            triangles = targetMeshFilter.sharedMesh.triangles;
        }
    }

    /// <summary>
    /// Instantiates a real GameObject using the generated mesh data
    /// </summary>
    private GameObject CreateSlicedObject(MeshData meshData, string name)
    {
        if (meshData.triangles.Count == 0) return null;

        // Build new Unity Mesh Object
        Mesh newMesh = new Mesh();
        newMesh.name = name;
        newMesh.vertices = meshData.vertices.ToArray();
        newMesh.triangles = meshData.triangles.ToArray();

        // Recalculate properties for lighting and rendering
        newMesh.RecalculateNormals();
        newMesh.RecalculateBounds();

        GameObject slicedObject = new GameObject(name);
        slicedObject.transform.position = transform.position;
        slicedObject.transform.rotation = transform.rotation;
        slicedObject.transform.localScale = transform.localScale;

        MeshFilter mf = slicedObject.AddComponent<MeshFilter>();
        mf.mesh = newMesh;

        MeshRenderer mr = slicedObject.AddComponent<MeshRenderer>();
        MeshRenderer sourceRenderer = GetComponent<MeshRenderer>();
        if (sourceRenderer != null)
        {
            mr.sharedMaterials = sourceRenderer.sharedMaterials;
        }

        return slicedObject;
    }

    /// <summary>
    /// Generates the filling geometry across the cut opened from the boundary
    /// </summary>
    /// <param name="aboveMesh">Mesh data container for the above plane object</param>
    /// <param name="belowMesh">Mesh data container for the below plane object</param>
    /// <param name="planeNormal">Direction of the normal of the cutting plane</param>
    private void CapMesh(MeshData aboveMesh, MeshData belowMesh, Vector3 planeNormal)
    {
        if (capVertices.Count < 3) return;

        Vector3 centroid = Vector3.zero;
        foreach (Vector3 point in capVertices)
        {
            centroid += point;
        }
        centroid /= capVertices.Count;

        Vector3 planeTangent = Vector3.Cross(planeNormal, Vector3.up);
        if (planeTangent.sqrMagnitude < 0.001f) // If the plane is pointing straight up or down
        {
            planeTangent = Vector3.Cross(planeNormal, Vector3.right);
        }
        planeTangent.Normalize();

        Vector3 planeBitangent = Vector3.Cross(planeNormal, planeTangent).normalized;

        capVertices.Sort((a, b) =>
        {
            Vector3 dirA = (a- centroid).normalized;
            float xA = Vector3.Dot(dirA, planeTangent);
            float yA = Vector3.Dot(dirA, planeBitangent);
            float angleA = Mathf.Atan2(yA, xA);

            Vector3 dirB = (b - centroid).normalized;
            float xB = Vector3.Dot(dirB, planeTangent);
            float yB = Vector3.Dot(dirB, planeBitangent);
            float angleB = Mathf.Atan2(yB, xB);

            return angleA.CompareTo(angleB);
        });

        for (int i = 0; i < capVertices.Count; i++)
        {
            Vector3 currentPoint = capVertices[i];
            Vector3 nextPoint = capVertices[(i + 1) % capVertices.Count]; // Wrap to 0 once it reaches the end

            // Above Mesh (facing down)
            int cAbove = aboveMesh.AddVertex(centroid);
            int currAbove = aboveMesh.AddVertex(currentPoint);
            int nextAbove = aboveMesh.AddVertex(nextPoint);
            aboveMesh.AddTriangle(cAbove, nextAbove, currAbove);

            // Below Mesh (facing up)
            int cBelow = belowMesh.AddVertex(centroid);
            int currBelow = belowMesh.AddVertex(currentPoint);
            int nextBelow = belowMesh.AddVertex(nextPoint);
            belowMesh.AddTriangle(cBelow, currBelow, nextBelow);
        }
    }
}
