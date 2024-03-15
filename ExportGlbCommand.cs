using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.FileIO;
using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using SYSENV = System.Environment;
using System.Collections.Generic;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using RHINOMESH = Rhino.Geometry.Mesh;
using static ExportGlb.MeshWithUserData;
using SharpGLTF.IO;
using SharpGLTF.Schema2;
using System.IO;
using Rhino.DocObjects;
using System.IO.Compression;


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
            var go = new GetObject();
            go.SetCommandPrompt("Select objects to mesh");
            go.GeometryFilter = ObjectType.Mesh | ObjectType.Brep;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            Rhino.RhinoApp.WriteLine("Please waiting...");

            var settings = new MeshingParameters(0); 
            List<MeshWithUserData> meshWithUserDataList = new List<MeshWithUserData>();

            foreach (var objRef in go.Objects())
            {
                List<UserAttribute> userAttributes = new List<UserAttribute>();
                var attributes = objRef.Object().Attributes;
                var keys = attributes.GetUserStrings();
                foreach (string key in keys)
                {
                    var value = attributes.GetUserString(key);
                    userAttributes.Add(new UserAttribute { key = key, value = value });
                }

                Rhino.DocObjects.Material rhinoMaterial = null;
                if (objRef.Object().Attributes.MaterialSource == ObjectMaterialSource.MaterialFromLayer)
                {
                    var layerIndex = objRef.Object().Attributes.LayerIndex;
                    var layer = doc.Layers[layerIndex];
                    if (layer.RenderMaterialIndex >= 0)
                    {
                        rhinoMaterial = doc.Materials[layer.RenderMaterialIndex];
                    }
                }
                else if (objRef.Object().Attributes.MaterialSource == ObjectMaterialSource.MaterialFromObject)
                {
                    rhinoMaterial = doc.Materials[objRef.Object().Attributes.MaterialIndex];
                }

                RHINOMESH mesh = null;
                if (objRef.Mesh() != null)
                {
                    mesh = objRef.Mesh();
                }
                else if (objRef.Brep() != null)
                {
                    var brepMeshes = RHINOMESH.CreateFromBrep(objRef.Brep(), settings);
                    if (brepMeshes.Length > 0)
                    {
                        mesh = new RHINOMESH();
                        foreach (var m in brepMeshes)
                        {
                            mesh.Append(m);
                        }
                    }
                }

                if (mesh != null && rhinoMaterial != null)
                {
                    meshWithUserDataList.Add(new MeshWithUserData(mesh, userAttributes, rhinoMaterial));
                }


            }

            var scaleFactor = GetModelScaleFactor(doc);
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
                    Rhino.RhinoApp.WriteLine(texture.ToString());
                    if (texture != null)
                    {
                        var texturePath = texture.FileReference?.FullPath;
                        Rhino.RhinoApp.WriteLine(texturePath);
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
                    var vertexA = CreateVertexBuilderWithUV(rhinoMesh, face.A);
                    var vertexB = CreateVertexBuilderWithUV(rhinoMesh, face.B);
                    var vertexC = CreateVertexBuilderWithUV(rhinoMesh, face.C);

                    prim.AddTriangle(vertexA, vertexB, vertexC);
                }
                
                var node = sceneBuilder.AddRigidMesh(meshBuilder, Matrix4x4.Identity);


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
            }


            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> CreateVertexBuilderWithUV(RHINOMESH mesh, int vertexIndex)
            {
                var position = mesh.Vertices[vertexIndex];
                var normal = mesh.Normals[vertexIndex];

                var uv = mesh.TextureCoordinates.Count > vertexIndex ? mesh.TextureCoordinates[vertexIndex] : Point2f.Unset;
                if (uv == Point2f.Unset) uv = new Point2f(0, 0);
                return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>
                    (
                        new VertexPositionNormal
                        (
                            position.X, position.Z, -position.Y, normal.X, normal.Z, -normal.Y
                        )
                        , new VertexTexture1(
                          new Vector2( uv.X, uv.Y)
                            )
                    );
            }


            var docPath = RhinoDoc.ActiveDoc.Path;
            var docName = string.IsNullOrEmpty(docPath) ? "untitled" : Path.GetFileNameWithoutExtension(docPath);
            var defaultFileName = $"{docName}.glb";

            var saveFileDialog = new Rhino.UI.SaveFileDialog
            {
                DefaultExt = "glb",
                FileName = defaultFileName,
                Filter = "GLB files (*.glb)|*.glb|GLTF files (*.gltf)|*.gltf|All files (*.*)|*.*",
                InitialDirectory = SYSENV.GetFolderPath(SYSENV.SpecialFolder.Desktop),
                Title = "Save GLB File"
            };

            if (!saveFileDialog.ShowSaveDialog())
            {
                return Result.Cancel;
            }
            var filePath = saveFileDialog.FileName;
            var fileFormat = Path.GetExtension(filePath).ToLower() == ".glb" ? "glb" : "gltf";
            var model = sceneBuilder.ToGltf2();
            if (fileFormat == "glb")
            {
                model.SaveGLB(filePath);
                Rhino.RhinoApp.WriteLine("GLBファイルを " + filePath + " に保存しました。");
            }
            else if (fileFormat == "gltf")
            {
                 var writeSettings = new WriteSettings
                {
                    JsonIndented = true,
                    MergeBuffers = true
                };
                model.SaveGLTF(filePath, writeSettings);
                Rhino.RhinoApp.WriteLine("GLTFファイルを " + filePath + " に保存しました。");
            }

            return Result.Success;
        }

        private static string GetImageFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            switch (extension)
            {
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                // 他のサポートされているフォーマットに応じて拡張可能
                default:
                    throw new NotSupportedException($"Unsupported image format: {extension}");
            }
        }

        private static float GetModelScaleFactor(RhinoDoc doc)
        {
            var modelUnit = doc.ModelUnitSystem;
            var scale = 1.0f;

            switch (modelUnit)
            {
                case UnitSystem.None:
                case UnitSystem.Meters:
                    scale = 1.0f;
                    break;
                case UnitSystem.Millimeters:
                    scale = 0.001f; 
                    break;
                case UnitSystem.Centimeters:
                    scale = 0.01f; 
                    break;
                case UnitSystem.Inches:
                    scale = 0.0254f;
                    break;
                case UnitSystem.Feet:
                    scale = 0.3048f;
                    break;
            }

            return scale;
        }


    }


    class MeshWithUserData
    {
        public RHINOMESH Mesh { get; set; }

        public class UserAttribute
        {
            public string key { get; set; }
            public string value { get; set; }
        }
        public List<UserAttribute> UserAttributes { get; set; }

        public Rhino.DocObjects.Material RhinoMaterial { get; set; }

        public MeshWithUserData(RHINOMESH mesh, List<UserAttribute> userAttributes, Rhino.DocObjects.Material rhinoMaterial)
        {
            Mesh = mesh;
            UserAttributes = userAttributes;
            RhinoMaterial = rhinoMaterial;
        }
    }
}
