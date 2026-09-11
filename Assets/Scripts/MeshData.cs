
using System.Collections.Generic;
using UnityEngine;

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
}