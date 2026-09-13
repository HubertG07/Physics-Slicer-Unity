
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/*
/// <summary>
/// Container for constructing dyn amic mesh vertices and triangle indices
/// </summary>
public class MeshData
{
    public List<Vector3> vertices = new List<Vector3>();
    public List<Vector2> uvs = new List<Vector2>();
    public List<int> triangles = new List<int>();

    // Helper to add a vertex and find its index
    public int AddVertex(Vector3 position, Vector2 uv)
    {
        vertices.Add(position);
        uvs.Add(uv);
        return vertices.Count - 1;
    }

    // Helper to add a triangle using three vertices
    public void AddTriangle(int indice0, int indice1, int indice2)
    {
        triangles.Add(indice0);
        triangles.Add(indice1);
        triangles.Add(indice2);
    }
}*/ // Old MeshData (Unoptimised)

/// <summary>
/// Container for dynamic mesh vertices optimised using Native Collections
/// </summary>
public struct MeshData
{
    public NativeList<Vector3> vertices;
    public NativeList<Vector2> uvs;
    public NativeList<int> triangles;

    public MeshData(int initialCapacity, Allocator allocator = Allocator.Persistent)
    {
        vertices = new NativeList<Vector3>(initialCapacity, allocator);
        uvs = new NativeList<Vector2>(initialCapacity, allocator);
        triangles = new NativeList<int>(initialCapacity, allocator);
    }

    /// <summary>
    /// Resets the list counters to 0 without freezing memory allocations
    /// </summary>
    public void Clear()
    {
        if (vertices.IsCreated) vertices.Clear();
        if (uvs.IsCreated) uvs.Clear();
        if (triangles.IsCreated) triangles.Clear();
    }

    /// <summary>
    /// Helper to add a vertex and return its index
    /// </summary>
    public int AddVertex(Vector3 position, Vector2 uv)
    {
        int index = vertices.Length;
        vertices.Add(position);
        uvs.Add(uv);
        return index;
    }

    /// <summary>
    /// Helper to add a triangle using three vertex indices
    /// </summary>
    public void AddTriangle(int indice0, int indice1, int indice2)
    {
        triangles.Add(indice0);
        triangles.Add(indice1);
        triangles.Add(indice2);
    }

    /// <summary>
    /// Frees up unmanaged memory buffers to prevent memory leaks
    /// </summary>
    public void Dispose()
    {
        if (vertices.IsCreated) vertices.Dispose();
        if (uvs.IsCreated) uvs.Dispose();
        if (triangles.IsCreated) triangles.Dispose();
    }

}