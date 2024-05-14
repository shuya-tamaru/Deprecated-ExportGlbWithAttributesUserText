using Rhino.Geometry;
using RHINOMESH = Rhino.Geometry.Mesh;

namespace ExportGlb.Models
{
    public static class GeometryValidator
    {
        public static bool IsValidBrep(Brep brep)
        {
            return brep != null && brep.Surfaces.Count > 0 && brep.GetArea() > 0;
        }

        public static bool IsValidMesh(RHINOMESH mesh)
        {
            return mesh != null && mesh.Faces.Count > 0 && mesh.Vertices.Count > 0;
        }
    }

}
