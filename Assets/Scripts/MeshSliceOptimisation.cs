using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

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
    private NativeList<Vector3> capVertices;

    private MeshData aboveMesh;
    private MeshData belowMesh;

    private InputAction eAction;

    void Awake()
    {
        CacheComponents();

        // Allocate the unmanaged NativeLists
        int initialCapacity = localVertices != null ? localVertices.Length : 256;
        aboveMesh = new MeshData(initialCapacity, Allocator.Persistent);
        belowMesh = new MeshData(initialCapacity, Allocator.Persistent);

        capVertices = new NativeList<Vector3>(64, Allocator.Persistent); 

        eAction = new InputAction(binding: "<Keyboard>/e");
        eAction.performed += OnEPressed;
    }

    private void OnEnable() => eAction.Enable();
    private void OnDisable() => eAction.Disable();

    private void OnDestroy()
    {
        eAction.performed -= OnEPressed;
        eAction.Dispose();

        aboveMesh.Dispose();
        belowMesh.Dispose();

        capVertices.Dispose();
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

        if (!capVertices.IsCreated) capVertices = new NativeList<Vector3>(64, Allocator.Persistent);
        else capVertices.Clear();

        if (!aboveMesh.vertices.IsCreated) aboveMesh = new MeshData(localVertices.Length, Allocator.Persistent);
        else aboveMesh.Clear();

        if (!belowMesh.vertices.IsCreated) belowMesh = new MeshData(localVertices.Length, Allocator.Persistent);
        else belowMesh.Clear();

        Vector3 localPlanePosition = transform.InverseTransformPoint(planeTransform.position);
        Vector3 localPlaneNormal = transform.InverseTransformDirection(planeTransform.up).normalized;

        int vertexCount = localVertices.Length;
        int totalTriangles = triangles.Length / 3;

        // Allocate unmanaged NativeArrays
        NativeArray<Vector3> nativeVertices = new NativeArray<Vector3>(localVertices, Allocator.TempJob);
        NativeArray<Vector2> nativeUVs = new NativeArray<Vector2>(localUVs, Allocator.TempJob);
        NativeArray<int> nativeTriangles = new NativeArray<int>(triangles, Allocator.TempJob);
        NativeArray<float> nativeDistances = new NativeArray<float>(vertexCount, Allocator.TempJob);

        // Schedule & Execute the burst compiled job
        ClassifyVerticesJob classifyJob = new ClassifyVerticesJob
        {
            Vertices = nativeVertices,
            LocalPlanePosition = localPlanePosition,
            LocalPlaneNormal = localPlaneNormal,
            OutDistances = nativeDistances
        };

        NativeQueue<SplitTriangleResult> resultQueue = new NativeQueue<SplitTriangleResult>(Allocator.TempJob);
        NativeQueue<CapPointPair> capQueue = new NativeQueue<CapPointPair>(Allocator.TempJob);

        

        SplitTranglesJob splitJob = new SplitTranglesJob
        {
            Vertices = nativeVertices,
            UVs = nativeUVs,
            Triangles = nativeTriangles,
            Distances = nativeDistances,
            OutTriangles = resultQueue.AsParallelWriter(),
            OutCapPoints = capQueue.AsParallelWriter()
        };

        // Classification and splitting across worker threads
        JobHandle classifyHandle = classifyJob.Schedule(vertexCount, 64);
        JobHandle splitHandle = splitJob.Schedule(totalTriangles, 32, classifyHandle);
        splitHandle.Complete();

        // Drain Results Linearly into the containers
        while (resultQueue.TryDequeue(out SplitTriangleResult result))
        {
            MeshData targetMesh = result.IsAbove ? aboveMesh : belowMesh;

            AddUncutTriangle(targetMesh, result.V0.Position, result.V0.UV,
                                        result.V1.Position, result.V1.UV,
                                        result.V2.Position, result.V2.UV);
            if (result.TriangleCount == 2)
            {
                AddUncutTriangle(targetMesh, result.V3.Position, result.V3.UV,
                                            result.V4.Position, result.V4.UV,
                                            result.V5.Position, result.V5.UV);
            }
        }

        while (capQueue.TryDequeue(out CapPointPair capPair))
        {
            capVertices.Add(capPair.PointA);
            capVertices.Add(capPair.PointB);
        }

        // Dispose native memory to prevent a memory leak
        nativeVertices.Dispose();
        nativeUVs.Dispose();
        nativeTriangles.Dispose();
        nativeDistances.Dispose();
        resultQueue.Dispose();
        capQueue.Dispose();


        // Fill the exposed surface boundary
        //CapMesh(aboveMesh, belowMesh, localPlaneNormal);
        BuildCapMeshJob capJob = new BuildCapMeshJob
        {
            CapPoints = capVertices.AsArray(),
            PlaneNormal = localPlaneNormal,
            OutVerticesAbove = aboveMesh.vertices,
            OutUVsAbove = aboveMesh.uvs,
            OutTrianglesAbove = aboveMesh.triangles,
            OutVerticesBelow = belowMesh.vertices,
            OutUVsBelow = belowMesh.uvs,
            OutTrianglesBelow = belowMesh.triangles
        };
        capJob.Schedule().Complete();

        // Check to ensure valid geometry was made
        if (aboveMesh.triangles.Count == 0 || belowMesh.triangles.Count == 0) return;

        float volumeAbove = CalculateMeshVolume(aboveMesh.vertices.AsArray(), aboveMesh.triangles.AsArray());
        float volumeBelow = CalculateMeshVolume(belowMesh.vertices.AsArray(), belowMesh.triangles.AsArray());
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
        
        int vertexCount = meshData.vertices.Length;
        int indexCount = meshData.triangles.Length;

        if (vertexCount == 0 || vertexCount == 0) return null;

        Vector3 localCenterOfMass = CalculateCentroid(meshData.vertices.AsArray());

        NativeArray<VertexLayout> vertexBuffer = new NativeArray<VertexLayout>(vertexCount, Allocator.Temp);
        for (int i = 0; i < vertexCount; i++)
        {
            vertexBuffer[i] = new VertexLayout
            {
                Position = meshData.vertices[i] - localCenterOfMass,
                UV = meshData.uvs[i]
            };
        }

        // Build new Unity Mesh Object
        Mesh newMesh = new Mesh();
        newMesh.name = name + "_Mesh";

        NativeArray<VertexAttributeDescriptor> layout = new NativeArray<VertexAttributeDescriptor>(2, Allocator.Temp);
        layout[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
        layout[1] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);

        newMesh.SetVertexBufferParams(vertexCount, layout);
        newMesh.SetVertexBufferData(vertexBuffer, 0, 0, vertexCount);

        newMesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
        newMesh.SetIndexBufferData(meshData.triangles.AsArray(), 0, 0, indexCount);

        newMesh.subMeshCount = 1;
        newMesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount));

        layout.Dispose();
        vertexBuffer.Dispose();

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
    /*private void CapMesh(MeshData aboveMesh, MeshData belowMesh, Vector3 planeNormal)
    {
        int capCount = capVertices.Length;
        if (capCount < 3) return;

        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < capCount; i++)
        {
            centroid += capVertices[i];
        }
        centroid /= capCount;

        Vector3 planeTangent = Vector3.Cross(planeNormal, Vector3.up);
        if (planeTangent.sqrMagnitude < 0.001f) // If the plane is pointing straight up or down
        {
            planeTangent = Vector3.Cross(planeNormal, Vector3.right);
        }
        planeTangent.Normalize();

        Vector3 planeBitangent = Vector3.Cross(planeNormal, planeTangent).normalized;

        for (int i = 0; i < capCount; i++)
        {
            int minIdx = i;
            float minAngle = GetAngle(capVertices[i], centroid, planeTangent, planeBitangent);

            for (int j = i + 1; j < capCount; j++)
            {
                float angle = GetAngle(capVertices[j], centroid, planeTangent, planeBitangent);
                if (angle < minAngle)
                {
                    minAngle = angle;
                    minIdx = j;
                }
            }

            if (minIdx != i)
            {
                Vector3 temp = capVertices[i];
                capVertices[i] = capVertices[minIdx];
                capVertices[minIdx] = temp;
            }
        }


        for (int i = 0; i < capCount; i++)
        {
            Vector3 currentPoint = capVertices[i];
            Vector3 nextPoint = capVertices[(i + 1) % capCount]; // Wrap to 0 once it reaches the end

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
    }*/ // Unused old function, replaced with BuildCapMeshJob

    private static float GetAngle(Vector3 point, Vector3 centroid, Vector3 tangent, Vector3 bitangent)
    {
        Vector3 dir = (point - centroid).normalized;
        float x = Vector3.Dot(dir, tangent);
        float y = Vector3.Dot(dir, bitangent);
        return Mathf.Atan2(y, x);
    }

    /// <summary>
    /// Calculate local center of mass of sub mesh verticies
    /// </summary>
    private Vector3 CalculateCentroid(NativeArray<Vector3> vertices)
    {
        Vector3 sum = Vector3.zero;
        for (int i =0; i < vertices.Length; i++)
        {
            sum += vertices[i];
        }
        return sum / vertices.Length;
    }
    
    /// <summary>
    /// Calculate the volume of a closed triangle mesh using tetrahedral decomposition
    /// </summary>
    private float CalculateMeshVolume(NativeArray<Vector3> verts, NativeArray<int> tris)
    {
        float volume = 0f;
        for (int i = 0; i < tris.Length; i += 3)
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

[BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
public struct SplitTranglesJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<Vector3> Vertices;
    [ReadOnly] public NativeArray<Vector2> UVs;
    [ReadOnly] public NativeArray<int> Triangles;
    [ReadOnly] public NativeArray<float> Distances;

    public NativeQueue<SplitTriangleResult>.ParallelWriter OutTriangles;
    public NativeQueue<CapPointPair>.ParallelWriter OutCapPoints;

    public void Execute(int index)
    {
        int triIdx = index * 3;
        int idx0 = Triangles[triIdx];
        int idx1 = Triangles[triIdx + 1];
        int idx2 = Triangles[triIdx + 2];

        Vector3 vertex0 = Vertices[idx0]; Vector3 vertex1 = Vertices[idx1]; Vector3 vertex2 = Vertices[idx2];
        Vector2 uv0 = UVs[idx0]; Vector2 uv1 = UVs[idx1]; Vector2 uv2 = UVs[idx2];
        float dist0 = Distances[idx0]; float dist1 = Distances[idx1]; float dist2 = Distances[idx2];

        // All Above
        if (dist0 >= 0 && dist1 >= 0 && dist2 >= 0)
        {
            OutTriangles.Enqueue(new SplitTriangleResult
            {
                V0 = new CutVertex { Position = vertex0, UV = uv0 },
                V1 = new CutVertex { Position = vertex1, UV = uv1 },
                V2 = new CutVertex { Position = vertex2, UV = uv2 },
                TriangleCount = 1,
                IsAbove = true
            });
        }
        // All Below
        else if (dist0 < 0 && dist1 < 0 && dist2 < 0)
        {
            OutTriangles.Enqueue(new SplitTriangleResult
            {
                V0 = new CutVertex { Position = vertex0, UV = uv0 },
                V1 = new CutVertex { Position = vertex1, UV = uv1 },
                V3 = new CutVertex { Position = vertex2, UV = uv2 },
                TriangleCount = 1,
                IsAbove = false
            });
        }
        // Split across the cut
        else
        {
            SplitSingleTriangle(vertex0, uv0, dist0, vertex1, uv1, dist1, vertex2, uv2, dist2);
        }
    }

    private void SplitSingleTriangle(Vector3 v0, Vector2 uv0, float d0,
                                    Vector3 v1, Vector2 uv1, float d1,
                                    Vector3 v2, Vector2 uv2, float d2)
    {
        Vector3 loneVert, pair1Vert, pair2Vert;
        Vector2 loneUV, pair1UV, pair2UV;
        float loneDist, pair1Dist, pair2Dist;

        if ((d0 >= 0 && d1 < 0 && d2 < 0) || (d0 < 0 && d1 >= 0 && d2 >= 0))
        {
            loneVert = v0; loneUV = uv0; loneDist = d0;
            pair1Vert = v1; pair1UV = uv1; pair1Dist = d1;
            pair2Vert = v2; pair2UV = uv2; pair2Dist = d2;
        }
        else if ((d1 >= 0 && d0 < 0 && d2 < 0) || (d1 < 0 && d0 >= 0 && d2 >= 0))
        {
            loneVert = v1; loneUV = uv1; loneDist = d1;
            pair1Vert = v2; pair1UV = uv2; pair1Dist = d2;
            pair2Vert = v0; pair2UV = uv0; pair2Dist = d0;
        }
        else
        {
            loneVert = v2; loneUV = uv2; loneDist = d2;
            pair1Vert = v0; pair1UV = uv0; pair1Dist = d0;
            pair2Vert = v1; pair2UV = uv1; pair2Dist = d1;
        }

        CutVertex cutA = Intersect(loneVert, loneUV, pair1Vert, pair1UV, loneDist, pair1Dist);
        CutVertex cutB = Intersect(loneVert, loneUV, pair2Vert, pair2UV, loneDist, pair2Dist);

        OutCapPoints.Enqueue(new CapPointPair { PointA = cutA.Position, PointB = cutB.Position });

        bool loneIsAbove = loneDist >= 0;

        // Lone side triangle
        OutTriangles.Enqueue(new SplitTriangleResult
        {
            V0 = new CutVertex { Position = loneVert, UV = loneUV },
            V1 = cutA,
            V2 = cutB,
            TriangleCount = 1,
            IsAbove = loneIsAbove
        });

        // Pair side quad
        OutTriangles.Enqueue(new SplitTriangleResult
        {
            V0 = new CutVertex { Position = pair1Vert, UV = pair1UV },
            V1 = new CutVertex { Position = pair2Vert, UV = pair2UV },
            V2 = cutA,
            V3 = new CutVertex { Position = pair2Vert, UV = pair2UV },
            V4 = cutB,
            V5 = cutA,
            TriangleCount = 2,
            IsAbove = !loneIsAbove
        });
    }

    private CutVertex Intersect(Vector3 vA, Vector2 uvA, Vector3 vB, Vector2 uvB, float dA, float dB)
    {
        float denom = math.abs(dA) + math.abs(dB);
        if (denom < 0.000001f) return new CutVertex { Position = vA, UV = uvA };
        float t = math.abs(dA) / denom;
        return new CutVertex
        {
            Position = math.lerp(vA, vB, t),
            UV = math.lerp(uvA, uvB, t)
        };
    }
}

[BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Fast)]
public struct BuildCapMeshJob : IJob
{
    [ReadOnly] public NativeArray<Vector3> CapPoints;
    public Vector3 PlaneNormal;
    
    public NativeList<Vector3> OutVerticesAbove;
    public NativeList<Vector2> OutUVsAbove;
    public NativeList<int> OutTrianglesAbove;

    public NativeList<Vector3> OutVerticesBelow;
    public NativeList<Vector2> OutUVsBelow;
    public NativeList<int> OutTrianglesBelow;

    private struct SortedPoint
    {
        public Vector3 Position;
        public float Angle;
    }

    public void Execute()
    {
        int count = CapPoints.Length;
        if (count < 3) return;

        Vector3 centroid = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            centroid += CapPoints[i];
        }
        centroid /= count;

        Vector3 planeTangent = math.cross(PlaneNormal, new Vector3(0, 1, 0));
        if (math.lengthsq(planeTangent) < 0.001f)
        {
            planeTangent = math.cross(PlaneNormal, new Vector3(1, 0, 0));
        }
        planeTangent = math.normalize(planeTangent);
        Vector3 planeBitangent = math.normalize(math.cross(PlaneNormal, planeTangent));

        NativeArray<SortedPoint> sortedPoints = new NativeArray<SortedPoint>(count, Allocator.Temp);
        for (int i = 0; i < count; i++)
        {
            Vector3 point = CapPoints[i];
            Vector3 direction = point - centroid;
            float x = math.dot(direction, planeTangent);
            float y = math.dot(direction, planeBitangent);

            sortedPoints[i] = new SortedPoint
            {
                Position = point,
                Angle = math.atan2(y, x)
            };
        }

        QuickSort(sortedPoints, 0, count - 1);

        Vector2 cUV = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < count; i++)
        {
            Vector3 currentPoint = sortedPoints[i].Position;
            Vector3 nextPoint = sortedPoints[(i + 1) % count].Position;

            Vector2 currUV = new Vector2(math.dot((currentPoint - centroid), planeTangent), math.dot((currentPoint - centroid), planeBitangent));
            Vector2 nextUV = new Vector2(math.dot((nextPoint - centroid), planeTangent), math.dot((nextPoint - centroid), planeBitangent));

            // Above Mesh Cap
            int cAbove = AddVertex(OutVerticesAbove, OutUVsAbove, centroid, cUV);
            int currAbove = AddVertex(OutVerticesAbove, OutUVsAbove, currentPoint, currUV);
            int nextAbove = AddVertex(OutVerticesAbove, OutUVsAbove, nextPoint, nextUV);
            AddTriangle(OutTrianglesAbove, cAbove, nextAbove, currAbove);

            // Below Mesh Cap
            int cBelow = AddVertex(OutVerticesBelow, OutUVsBelow, centroid, cUV);
            int currBelow = AddVertex(OutVerticesBelow, OutUVsBelow, currentPoint, currUV);
            int nextBelow = AddVertex(OutVerticesBelow, OutUVsBelow, nextPoint, nextUV);
            // FIX 2: Target OutTrianglesBelow and adjust winding order for inverted normal
            AddTriangle(OutTrianglesBelow, cBelow, currBelow, nextBelow); 
        }

        sortedPoints.Dispose();
    }

    private int AddVertex(NativeList<Vector3> verts, NativeList<Vector2> uvs, Vector3 position, Vector2 uv)
    {
        verts.Add(position);
        uvs.Add(uv);
        return verts.Length - 1;
    }

    private void AddTriangle(NativeList<int> tris, int indice0, int indice1, int indice2)
    {
        tris.Add(indice0);
        tris.Add(indice1);
        tris.Add(indice2);
    }

    private void QuickSort(NativeArray<SortedPoint> arr, int left, int right)
    {
        if (left >= right) return;
        float pivot = arr[(left + right) / 2].Angle;
        int i = left, j = right;

        while (i <= j)
        {
            while (arr[i].Angle < pivot) i++;
            while (arr[j].Angle > pivot) j--;
            if (i <= j)
            {
                SortedPoint temp = arr[i];
                arr[i] = arr[j];
                arr[j] = temp;
                i++;
                j--;
            }
        }

        if (left < j) QuickSort(arr, left, j);
        if (i < right) QuickSort(arr, i, right);
    }
}

public struct CutVertex
{
    public Vector3 Position;
    public Vector2 UV;
}

public struct SplitTriangleResult
{
    // Up to two triangles get created
    public CutVertex V0, V1, V2;
    public CutVertex V3, V4, V5;
    public int TriangleCount; // 1 or 2
    public bool IsAbove;
}

public struct CapPointPair
{
    public Vector3 PointA;
    public Vector3 PointB;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct VertexLayout
{
    public Vector3 Position;
    public Vector2 UV;
}