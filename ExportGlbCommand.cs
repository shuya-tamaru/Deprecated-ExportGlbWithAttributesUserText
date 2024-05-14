using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.DocObjects;
using System.Numerics;
using System.Collections.Generic;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using RHINOMESH = Rhino.Geometry.Mesh;
using SharpGLTF.IO;
using System.IO;
using ExportGlb.Models;
using ExportGlb.Utilities;
using ExportGlb.Helpers;


namespace ExportGlb
{
    public class ExportGlbCommand : Command
    {
        public ExportGlbCommand()
        {
            Instance = this;
        }

        public static ExportGlbCommand Instance { get; private set; }

        public override string EnglishName => "GlbWithUserAttributes";


        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var geometry = new GetObject();
            geometry.SetCommandPrompt("Select objects to mesh");
            geometry.GeometryFilter = ObjectType.Mesh | ObjectType.Brep;
            geometry.SubObjectSelect = false;
            geometry.GroupSelect = true;
            geometry.GetMultiple(1, 0);
            if (geometry.CommandResult() != Result.Success)
            {
                Rhino.RhinoApp.WriteLine("An error occurred: " + geometry.CommandResult().ToString());
                return geometry.CommandResult();
            }

            Rhino.RhinoApp.WriteLine("Please waiting...");

            var settings = new MeshingParameters(0); 
            List<MeshWithUserData> meshWithUserDataList = new List<MeshWithUserData>();

            Rhino.DocObjects.Material defaultMaterial = new Rhino.DocObjects.Material();
            defaultMaterial.Name = "Default";
            defaultMaterial.DiffuseColor = System.Drawing.Color.White;

            foreach (var objRef in geometry.Objects())
            {
                try {
                    //Convert Brep to Mesh
                    RHINOMESH mesh = null;
                    if (objRef.Mesh() != null)
                    {
                        mesh = objRef.Mesh();
                    }
                    else if (objRef.Brep() != null)
                    {
                        var brep = objRef.Brep();
                        if (!GeometryValidator.IsValidBrep(brep))
                        {
                            continue;
                        }
                        var brepMeshes = RHINOMESH.CreateFromBrep(brep, settings);
                        if (brepMeshes != null && brepMeshes.Length > 0)
                        {
                            mesh = new RHINOMESH();
                            foreach (var m in brepMeshes)
                            {
                                mesh.Append(m);
                            }
                        }
                    }

                    if (mesh == null || !GeometryValidator.IsValidMesh(mesh))
                    {
                        continue;
                    }

                    //Get Material
                    Rhino.DocObjects.Material rhinoMaterial = defaultMaterial;
                    if (objRef.Object().Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject)
                    {
                        rhinoMaterial = doc.Materials[objRef.Object().Attributes.MaterialIndex];
                    }
                    else if (objRef.Object().Attributes.MaterialSource == ObjectMaterialSource.MaterialFromLayer)
                    {
                        var layerIndex = objRef.Object().Attributes.LayerIndex;
                        var layer = doc.Layers[layerIndex];
                        if (layer.RenderMaterialIndex >= 0)
                        {
                            rhinoMaterial = doc.Materials[layer.RenderMaterialIndex];
                        }
                    }

                    //Get UserAttributes
                    List<UserAttribute> userAttributes = new List<UserAttribute>();
                    var attributes = objRef.Object().Attributes;
                    var keys = attributes.GetUserStrings();
                    foreach (string key in keys)
                    {
                        var value = attributes.GetUserString(key);
                        userAttributes.Add(new UserAttribute { key = key, value = value });
                    }

                    //Add MeshWithUserData to list
                    if (mesh != null && rhinoMaterial != null)
                    {
                        meshWithUserDataList.Add(new MeshWithUserData(mesh, userAttributes, rhinoMaterial));
                    }

                }
                catch (System.Exception ex)
                {
                    Rhino.RhinoApp.WriteLine("An error occurred: " + ex.Message);
                    return Result.Failure;
                }
            }

            float scaleFactor = Utility.GetModelScaleFactor(RhinoDoc.ActiveDoc);
            Dictionary<string, MaterialBuilder> materialBuilders = new Dictionary<string, MaterialBuilder>();
            var sceneBuilder = new SceneBuilder();

            foreach (var item in meshWithUserDataList)
            {
                RHINOMESH rhinoMesh = item.Mesh;
                var scaleTransform = Rhino.Geometry.Transform.Scale(Point3d.Origin, scaleFactor);
                rhinoMesh.Transform(scaleTransform);

                List<UserAttribute> attributes = item.UserAttributes; 
                Rhino.DocObjects.Material rhinoMaterial = item.RhinoMaterial;

                string materialName = !string.IsNullOrEmpty(rhinoMaterial.Name) ? rhinoMaterial.Name : "Material_" + materialBuilders.Count.ToString();
                if (!materialBuilders.TryGetValue(materialName, out MaterialBuilder materialBuilder))
                {
                    materialBuilder = new MaterialBuilder(materialName)
                        .WithDoubleSide(true)
                        .WithMetallicRoughnessShader()
                        .WithChannelParam(KnownChannel.BaseColor, new Vector4(
                            (float)rhinoMaterial.DiffuseColor.R / 255,
                            (float)rhinoMaterial.DiffuseColor.G / 255,
                            (float)rhinoMaterial.DiffuseColor.B / 255,
                            1.0f - (float)rhinoMaterial.Transparency
                        ));

                    var texture = rhinoMaterial.GetBitmapTexture();
                    if (texture != null)
                    {
                        var texturePath = texture.FileReference?.FullPath;
                        if (!string.IsNullOrEmpty(texturePath) && File.Exists(texturePath))
                        {
                            
                            materialBuilder.WithChannelImage(KnownChannel.BaseColor, texturePath);
                        }
                    }
                    materialBuilders.Add(materialName, materialBuilder);
                }

                var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1>("mesh");
                var prim = meshBuilder.UsePrimitive(materialBuilder);

                rhinoMesh.Faces.ConvertQuadsToTriangles(); 
                rhinoMesh.Normals.ComputeNormals();
                rhinoMesh.Compact();

                foreach (var face in rhinoMesh.Faces)
                {
                    var vertexA = VertexUtility.CreateVertexBuilderWithUV(rhinoMesh, face.A);
                    var vertexB = VertexUtility.CreateVertexBuilderWithUV(rhinoMesh, face.B);
                    var vertexC = VertexUtility.CreateVertexBuilderWithUV(rhinoMesh, face.C);

                    prim.AddTriangle(vertexA, vertexB, vertexC);
                }

                if (attributes.Count > 0)
                {
                    Dictionary<string, string> attributesDict = new Dictionary<string, string>();
                    foreach (var attribute in item.UserAttributes)
                    {
                        var key = attribute.key;
                        var value = attribute.value;
                        attributesDict.Add(key, value);
                    }

                    var extras = JsonContent.CreateFrom(attributesDict);
                    meshBuilder.Extras = extras;
                }

                sceneBuilder.AddRigidMesh(meshBuilder, Matrix4x4.Identity);
            }


            //Export glb or gltf file
            bool isSaved = FileSaver.ShowSaveFileDialogAndSave(sceneBuilder, doc);
            if (!isSaved)
            {
                Rhino.RhinoApp.WriteLine("File saving cancelled or failed.");
                return Result.Cancel;
            }

            return Result.Success;
        }

    }
 }
