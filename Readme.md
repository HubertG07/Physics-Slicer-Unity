# Real-Time Mesh Physics Slicer (Unity)
> A lightweight, real-time procedural mesh slicing framework build in C# for Unity. The project cuts any 3D geometric shapre across a dynamic cutting plane and reconstructs clean, independant sub-meshes with proper topology and recalculated surface normals.
---

## Preview & Demo
Waiting for project to finish before adding the gif.

*Demonstation of the dynamic mesh slicing, plane intersection and mesh seperation.
---

## Project Overview & Summary
The **Physics Slicer** is a procedural mesh manipulation system designed to slice a 3D object dynmaically during runtime. It uses vector geometry, plane-space calculations and vertex reconstruction without relying of pre-fractured assets.

### Key Features
* **No Precomputing:** Operates on the raw mesh data at runtime.
* **Local-Space Optimization:** Executes matrix transformations inside the local object space to minimize CPU/GPU load.
* **Dynamic Retriangulation:** Handles 1 to 3 triangle splitting while maintaining proper orders for lighting.
* **Modular Pipeline Architecture:** Stages split across multiple test scripts to ensure accuracy and progress at every stage.
---

## Project Logs and Time Tracking
Breakdown of the time invested during development
| Date | Time Window | Session Duration | Focus Area |
| --- | --- | --- | --- |
| **10th Sept 2026** | 18:25-20:54 | 2 hrs 29 mins | Plane dot product math & Dymanic mesh reconstruction (Stages 1-3) |
| **11th Sept 2026** | 14:30-TBD | TBD | Cap filling. physics Rigidbody generation & potentially more |
| **Future** | TBD | TBD | Slice Force Impulses + Test Scene + Adjustable settings (if a force is applied etc) |

* **Project Start Date:** September 10th 2026
* **Project Finish Date:** Not yet completed
* **Current Total Time:** ~2.5 Hours (Ongoing)
---

## Technical Breakdown & Architecture
Currently the pipeline operates on 3-stage modular workflow

### Stage 1: Local-Space Vertex Classification (MeshSliceTest.cs):
* **Objective:** Determine which side of the plane, every mesh vertex resides on.
* **Technical Overview:** Transforms the cutting plane's position and normal vector into the mesh's local space using `InverseTransformPoint` and `InverseTransformDirection`. Using the Dot Product equation:

$$d = (P_{\text{vertex}} - P_{\text{plane}}) \cdot \mathbf{N}_{\text{plane}}$$

* **Clasification Rule:**
    * $d \ge 0$: Vertex is infront or above the plane (Green Gizmo).
    * $d < 0$: Vertex is behind or below the plane (Red Gizmo).

## Stage 2: Triangle Edge/Plane Intersection (MeshSliceTriangles.cs):
* **Objective:** Detect where the triangle edges cross the cutting boundary and find the split coordinates.
* **Technical Overview:** Iterates over the mesh index (`triangles`). Tests each of the 3 edges per face using the signed distance product:

$$\text{isCut} = (distA \cdot distB < 0)$$

* **Linear Interpolation ($t$-value):** Calculates the exact intersection coordinates using distance ratio weighing:

$$t = \frac{|distA|}{|distA| + |distB|}$$

$$P_{\text{cut}} = \text{Vector3.Lerp}(VertexA, VertexB, t)$$

## Stage 3: Dynamic Mesh Reconstruction (MeshSliceReconstruction.cs):
* **Objective:** Split crossed triangles into valid mesh sub-components and rebuild a new `GameObject` instance.
* **Technical Overview:** * Uncut triangles pass directly to `AboveMesh` or `BelowMesh` buffers.
    * Intersected triangles split into 1 sub-triangle on the isolated vertex side and 2 sub-triangles (quadrilateral) on the paired vertex side
    * Preserves clockwise winding orders to surface the normals and lighting correctly
    * Recalculates the bounding boxes (`RecalculateBounds()` and surface normals (`RecalculateNormal()`))

## Stage 4: Hole Filling & Surface Capping (MeshSliceFilling.cs):
* **Objective:** Seal the open surface boundary created by the slicing operations
* **Technical Overview:**
    * **Centroid Computing:** Average the collected edge intersection points to generate a central vertex ($C$)
    * **2D Plane Projection:** Derive the local tangent and bitangent vectors using the cross product to conver the 3D edge points into 2D planar offsets
    * **Polar Angle Sorting:** Order the boundary vertices counter-clockwise using `Mathf.Atan2(y, x)`relative to the tangent plane
    * **Triangle Fan Reconstruction:** Triangulates the sorted points around the centroid, reversing the index winding orders between the top and bottom sub-meshes to ensure correctly facing normals

## Stage 5: UV Interpolation & Planar Projection Mapping (MeshSliceUV.cs):
* **Objective:** Preserve the original texture mapping acrossed the sliced edges and project dynamic 2D texture coordinates onto the newly capped faces
* **Technical Overview:**
    * **Edge UV Interpolation:** Evaluate the UV coordinates at the intersection points using dual linear interpolation (`Vector2.Lerp`) weighted by the scalar distance ratio $t$
    * **Planar Cap Projection:** Generate the capping UVs by projecting the 3D point offsets relative to the centroid onto the local orthogonal tangent axes ($\mathbf{U}, \,mathbf{V}$):
    $$\text{UV}_{\text{cap}} = \left((P - C) \cdot \mathbf{U}, \, (P - C) \cdot \mathbf{V}\right)$$
    * **Surface Lighting:** Rebuild the mesh tangent vectors (`RecalculateTangents()`) to ensure directional lighting and normal maps across the sub-meshes
---
## Takeaways & Learnings
Building this slicer helped me learn critical low-level 3D graphics and physics concepts:
1. **Coordinate Space Efficiency:** Performing the dot product on the vector in local object space helped to elimate the need of transforming thousands of mesh vertices into world space every frame.
2. **Index Buffer Winding:** Re-indexing the triangles programmatically required a lot of attention to the clockwise order.
3. **Memory Management:** Constructing temporary dynamic meshes required clean buffer instantialtion (`Vector3[]`, `int[]`) before calling `.ToArray()` to minimize GC allocations.
---

## How to Run & Usage (WIP)
> **Note:** The project is still in active development. Usage instructions will be updated upon final release.
### Stage 3 Instructions:
1. Attach `MeshSliceReconstruction.cs` to the target GameObject, containing a `MeshFilter` and `MeshRenderer`.
2. Reference a target `Transform` plane as the `planeTransform` (what will be doing the cutting)
3. In Play Mode press the **[E]** key to trigger the slicing pipeline

Usage: Free to use without credit