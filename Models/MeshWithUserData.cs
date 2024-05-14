using System.Collections.Generic;
using Rhino.DocObjects;
using RHINOMESH = Rhino.Geometry.Mesh;

namespace ExportGlb.Models
{
    public class MeshWithUserData
    {
        public RHINOMESH Mesh { get; set; }
        public List<UserAttribute> UserAttributes { get; set; }
        public Material RhinoMaterial { get; set; }

        public MeshWithUserData(RHINOMESH mesh, List<UserAttribute> userAttributes, Material rhinoMaterial)
        {
            Mesh = mesh;
            UserAttributes = userAttributes;
            RhinoMaterial = rhinoMaterial;
        }
    }
}
