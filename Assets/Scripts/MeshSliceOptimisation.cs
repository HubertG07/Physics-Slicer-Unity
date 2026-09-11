using System.Collections.Generic;
using System.Net.Security;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SocialPlatforms;

/// <summary>
/// Slices and reconstructs a mesh into two seperate game objects along a plane boundary
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(Rigidbody))]
public class MeshSliceOptimisation : MonoBehaviour
{
    [SerializeField] private Transform planeTransform;
    
    [Header("Physics Settings")]
    [Tooltip("Impulse force applied to the cut plane to push the sliced pieces apart")]
    [SerializeField] private float seperationImpulse = 3.0f;

    [Tooltip("Original mass of the object before slicing. DEFAULTS TO RIGIDBODY MASS if set.")]
    [SerializeField] private float originalMass = 1.0f;

    private MeshFilter targetMeshFilter;
    private MeshRenderer targetMeshRenderer;
    private Rigidbody targetRigidbody;
    private Vector3[] localVertices;
    private Vector2[] localUVs;
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

        int vertexCount = localVertices.Length;

        // Allocate unmanaged NativeArrays
        NativeArray<Vector3> nativeVertices = new NativeArray<Vector3>(localVertices, Allocator.TempJob);
        NativeArray<float> nativeDistances = new NativeArray<float>(vertexCount, Allocator.TempJob);

        // Schedule & Execute the burst compiled job
        ClassifyVerticesJob classifyJob = new ClassifyVerticesJob
        {
            Vertices = nativeVertices,
            LocalPlanePosition = localPlanePosition,
            LocalPlaneNormal = localPlaneNormal,
            OutDistances = nativeDistances
        };

        JobHandle jobHandle = classifyJob.Schedule(vertexCount, 64);
        jobHandle.Complete(); // Block until the parallel execution finishes

        for (int i = 0; i < triangles.Length; i += 3)
        {
            
            int idx0 = triangles[i];
            int idx1 = triangles[i + 1];
            int idx2 = triangles[i + 2];

            // Fetch position of all 3 vertices of the triangle face
            Vector3 vertex0 = localVertices[idx0];
            Vector3 vertex1 = localVertices[idx1];
            Vector3 vertex2 = localVertices[idx2];

            Vector2 uv0 = localUVs[idx0];
            Vector2 uv1 = localUVs[idx1];
            Vector2 uv2 = localUVs[idx2];

            float dist0 = nativeDistances[idx0];
            float dist1 = nativeDistances[idx1];
            float dist2 = nativeDistances[idx2];

            if (dist0 >= 0 && dist1 >= 0 && dist2 >= 0)
            {
                // Uncut (everything is above)
                AddUncutTriangle(aboveMesh, vertex0, uv0, vertex1, uv1, vertex2, uv2);
            }
            else if (dist0 < 0 && dist1 < 0 && dist2 < 0)
            {
                //Uncut (everything below)
                AddUncutTriangle(belowMesh, vertex0, uv0, vertex1, uv1, vertex2, uv2);
            }
            else
            {
                // Split the triangles across the plane
                SplitTriangle(aboveMesh, belowMesh, vertex0, uv0, vertex1, uv1, vertex2, uv2, dist0, dist1, dist2);
            }
        }

        // Dispose native memory to prevent a memory leak
        nativeVertices.Dispose();
        nativeDistances.Dispose();

        // Fill the exposed surface boundary
        CapMesh(aboveMesh, belowMesh, localPlaneNormal);

        // Check to ensure valid geometry was made
        if (aboveMesh.triangles.Count == 0 || belowMesh.triangles.Count == 0) return;

        float volumeAbove = CalculateMeshVolume(aboveMesh.vertices, aboveMesh.triangles);
        float volumeBelow = CalculateMeshVolume(belowMesh.vertices, belowMesh.triangles);
        float totalVolume = Mathf.Max(0.0001f, volumeAbove + volumeBelow);

        float massAbove = originalMass * (volumeAbove / totalVolume);
        float massBelow = originalMass * (volumeBelow / totalVolume);


        // Convert above and below mesh into real mesh objects
        GameObject aboveObject = CreateSlicedObject(aboveMesh, $"{gameObject.name}_Above", massAbove, localPlaneNormal);
        GameObject belowObject = CreateSlicedObject(belowMesh, $"{gameObject.name}_Below", massBelow, -localPlaneNormal);

        gameObject.SetActive(false);
    }

    /// <summary>
    /// Add the unsplit triangle directly to the target mesh
    /// </summary>
    private void AddUncutTriangle(MeshData meshData,
                                    Vector3 vertex0, Vector2 uv0,
                                    Vector3 vertex1, Vector2 uv1,
                                    Vector3 vertex2, Vector2 uv2)
    {
        int indice0 = meshData.AddVertex(vertex0, uv0);
        int indice1 = meshData.AddVertex(vertex1, uv1);
        int indice2 = meshData.AddVertex(vertex2, uv2);
        meshData.AddTriangle(indice0, indice1, indice2);
    }

    /// <summary>
    /// Splits an intersecting triangle into smaller triangles across the plane boundary
    /// </summary>
    private void SplitTriangle(MeshData above, MeshData below,
                            Vector3 vertex0, Vector2 uv0,
                            Vector3 vertex1, Vector2 uv1,
                            Vector3 vertex2, Vector2 uv2,
                            float dist0, float dist1, float dist2)
    {
        Vector3 loneVert, pairVert1, pairVert2;
        Vector2 loneUV, pairUV1, pairUV2;
        float loneDist, pairDist1, pairDist2;

        // Identify which vertex is on its own side of the plane
        if ((dist0 >= 0 && dist1 < 0 && dist2 < 0) || (dist0 < 0 && dist1 >= 0 && dist2 >= 0))
        {
            loneVert = vertex0; loneUV = uv0; loneDist = dist0;
            pairVert1 = vertex1; pairUV1 = uv1; pairDist1 = dist1;
            pairVert2 = vertex2; pairUV2 = uv2; pairDist2 = dist2;
        }
        else if ((dist1 >= 0 && dist0 < 0 && dist2 < 0) || (dist1 < 0 && dist0 >= 0 && dist2 >= 0))
        {
            loneVert = vertex1; loneUV = uv1; loneDist = dist1;
            pairVert1 = vertex2; pairUV1 = uv2; pairDist1 = dist2;
            pairVert2 = vertex0; pairUV2 = uv0; pairDist2 = dist0;
        }
        else
        {
            loneVert = vertex2; loneUV = uv2; loneDist = dist2;
            pairVert1 = vertex0; pairUV1 = uv0; pairDist1 = dist0;
            pairVert2 = vertex1; pairUV2 = uv1; pairDist2 = dist1;
        }

        // Calculate the intersection points along the edges crossing the plane
        var cutA = IntersectEdge(loneVert, loneUV, pairVert1, pairUV1, loneDist, pairDist1);
        var cutB = IntersectEdge(loneVert, loneUV, pairVert2, pairUV2, loneDist, pairDist2);

        capVertices.Add(cutA.pos);
        capVertices.Add(cutB.pos);

        // Determine which side of the plane has the single vertex and which has the vertex pair
        MeshData loneSideMesh = (loneDist >= 0) ? above : below;
        MeshData pairSideMesh = (loneDist >= 0) ? below : above;

        AddUncutTriangle(loneSideMesh, loneVert, loneUV, cutA.pos, cutA.uv, cutB.pos, cutB.uv);

        int iPair1 = pairSideMesh.AddVertex(pairVert1, pairUV1);
        int iPair2 = pairSideMesh.AddVertex(pairVert2, pairUV2);
        int iCutA = pairSideMesh.AddVertex(cutA.pos, cutA.uv);
        int iCutB = pairSideMesh.AddVertex(cutB.pos, cutB.uv);

        pairSideMesh.AddTriangle(iPair1, iPair2, iCutA);
        pairSideMesh.AddTriangle(iPair2, iCutB, iCutA);
    }

    /// <summary>
    /// Calculate the intersection position and interpolate the UV coordinates along the edge of the vertices
    /// </summary>
    /// <param name="vertexA">Position of first vertex</param>
    /// <param name="uvA">UV coordinates at vertexA</param>
    /// <param name="vertexB">Position of second vertex</param>
    /// <param name="uvB">UV coordinates at vertexB</param>
    /// <param name="distA">Scalar distance at vertexA</param>
    /// <param name="distB">Scalar distance at vertexB</param>
    /// <returns>Interpolated position and UV pair where plane intersects edge</returns>
    private (Vector3 pos, Vector2 uv) IntersectEdge(Vector3 vertexA, Vector2 uvA, Vector3 vertexB, Vector2 uvB, float distA, float distB)
    {
        float denom = Mathf.Abs(distA) + Mathf.Abs(distB);
        if (denom < 0.000001f) return (vertexA, uvA);

        float t = Mathf.Abs(distA) / denom;
        Vector3 pos = Vector3.Lerp(vertexA, vertexB, t);
        Vector2 uv = Vector2.Lerp(uvA, uvB, t);
        return (pos, uv);
    }

    /// <summary>
    /// Fetches required local components
    /// </summary>
    private void CacheComponents()
    {
        targetMeshFilter = GetComponent<MeshFilter>();
        targetMeshRenderer = GetComponent<MeshRenderer>();
        targetRigidbody = GetComponent<Rigidbody>();

        if (targetRigidbody != null)
        {
            originalMass = targetRigidbody.mass;
        }

        if (targetMeshFilter != null && targetMeshFilter.sharedMesh != null)
        {
            localVertices = targetMeshFilter.sharedMesh.vertices;
            localUVs = targetMeshFilter.sharedMesh.uv;
            triangles = targetMeshFilter.sharedMesh.triangles;

            if (localUVs == null || localUVs.Length != localVertices.Length)
            {
                localUVs = new Vector2[localVertices.Length];
            }
        }
    }

    /// <summary>
    /// Instantiates a real GameObject using the generated mesh data
    /// </summary>
    private GameObject CreateSlicedObject(MeshData meshData, string name, float mass, Vector3 localImpulseNormal)
    {
        if (meshData.triangles.Count == 0) return null;

        Vector3 localCenterOfMass = CalculateCentroid(meshData.vertices);
        for (int i = 0; i < meshData.vertices.Count; i++)
        {
            meshData.vertices[i] -= localCenterOfMass;
        }

        // Build new Unity Mesh Object
        Mesh newMesh = new Mesh();
        newMesh.name = name + "_Mesh";
        newMesh.vertices = meshData.vertices.ToArray();
        newMesh.uv = meshData.uvs.ToArray();
        newMesh.triangles = meshData.triangles.ToArray();

        // Recalculate properties for lighting and rendering
        newMesh.RecalculateNormals();
        newMesh.RecalculateBounds();
        newMesh.RecalculateTangents();

        GameObject slicedObject = new GameObject(name);
        slicedObject.transform.position = transform.TransformPoint(localCenterOfMass);
        slicedObject.transform.rotation = transform.rotation;
        slicedObject.transform.localScale = transform.localScale;

        MeshFilter mf = slicedObject.AddComponent<MeshFilter>();
        mf.sharedMesh = newMesh;

        MeshRenderer mr = slicedObject.AddComponent<MeshRenderer>();
        if (targetMeshRenderer != null)
        {
            mr.sharedMaterial = targetMeshRenderer.sharedMaterial;
        }

        MeshCollider mc = slicedObject.AddComponent<MeshCollider>();
        mc.sharedMesh = newMesh;
        mc.convex = true;

        Rigidbody rb = slicedObject.AddComponent<Rigidbody>();
        rb.mass = Mathf.Max(0.05f, mass);

        // Recompute the inertia tensor relevative to new mesh
        rb.ResetCenterOfMass();
        rb.ResetInertiaTensor();

        if (targetRigidbody != null)
        {
            rb.linearVelocity = targetRigidbody.linearVelocity;
            rb.angularVelocity = targetRigidbody.angularVelocity;
        }

        Vector3 worldImpulseDirection = transform.TransformDirection(localImpulseNormal).normalized;
        rb.AddForce(worldImpulseDirection * seperationImpulse, ForceMode.Impulse);

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

            // Project positions relevative to centroid onto the tangent axes to generate the UVs
            Vector2 cUV = new Vector2(0.5f, 0.5f);
            Vector2 currUV = new Vector2(Vector3.Dot((currentPoint - centroid), planeTangent), Vector3.Dot((currentPoint - centroid), planeBitangent));
            Vector2 nextUV = new Vector2(Vector3.Dot((nextPoint - centroid), planeTangent), Vector3.Dot((nextPoint - centroid), planeBitangent));

            // Above Mesh (facing down)
            int cAbove = aboveMesh.AddVertex(centroid, cUV);
            int currAbove = aboveMesh.AddVertex(currentPoint, currUV);
            int nextAbove = aboveMesh.AddVertex(nextPoint, nextUV);
            aboveMesh.AddTriangle(cAbove, nextAbove, currAbove);

            // Below Mesh (facing up)
            int cBelow = belowMesh.AddVertex(centroid, cUV);
            int currBelow = belowMesh.AddVertex(currentPoint, currUV);
            int nextBelow = belowMesh.AddVertex(nextPoint, nextUV);
            belowMesh.AddTriangle(cBelow, currBelow, nextBelow);
        }
    }

    /// <summary>
    /// Calculate local center of mass of sub mesh verticies
    /// </summary>
    private Vector3 CalculateCentroid(List<Vector3> vertices)
    {
        Vector3 sum = Vector3.zero;
        for (int i =0; i < vertices.Count; i++)
        {
            sum += vertices[i];
        }
        return sum / vertices.Count;
    }
    
    /// <summary>
    /// Calculate the volume of a closed triangle mesh using tetrahedral decomposition
    /// </summary>
    private float CalculateMeshVolume(List<Vector3> verts, List<int> tris)
    {
        float volume = 0f;
        for (int i = 0; i < tris.Count; i += 3)
        {
            Vector3 p1 = verts[tris[i]];
            Vector3 p2 = verts[tris[i + 1]];
            Vector3 p3 = verts[tris[i + 2]];
            volume += Vector3.Dot(p1, Vector3.Cross(p2, p3)) / 6.0f;
        }
        return Mathf.Abs(volume);
    }
}

[BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct ClassifyVerticesJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Vector3> Vertices;
    public Vector3 LocalPlanePosition;
    public Vector3 LocalPlaneNormal;
    [WriteOnly] public NativeArray<float> OutDistances;

    public void Execute (int index)
    {
        Vector3 vertex = Vertices[index];
        OutDistances[index] = Vector3.Dot((vertex - LocalPlanePosition), LocalPlaneNormal);
    }
}