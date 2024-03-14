using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input.Custom;
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
            // ユーザーにオブジェクトの選択を促す
            var go = new GetObject();
            go.SetCommandPrompt("Select objects to mesh");
            go.GeometryFilter = ObjectType.Mesh | ObjectType.Brep;
            go.SubObjectSelect = false;
            go.GroupSelect = true;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success)
                return go.CommandResult();

            Rhino.RhinoApp.WriteLine("出力準備中");

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
                RHINOMESH rhinoMesh = item.Mesh; // Rhinoのメッシュ
                var scaleTransform = Rhino.Geometry.Transform.Scale(Point3d.Origin, scaleFactor);
                rhinoMesh.Transform(scaleTransform);

                List<UserAttribute> attributes = item.UserAttributes; // ユーザーデータリスト
                Rhino.DocObjects.Material rhinoMaterial = item.RhinoMaterial; // Rhinoのマテリアル

                // RhinoのマテリアルからSharpGLTFのマテリアルを構築
                string materialName = !string.IsNullOrEmpty(rhinoMaterial.Name) ? rhinoMaterial.Name : "Material_" + materialBuilders.Count.ToString();
                if (!materialBuilders.TryGetValue(materialName, out MaterialBuilder materialBuilder))
                {
                    // 新しいMaterialBuilderを作成し、辞書に追加します
                    materialBuilder = new MaterialBuilder(materialName)
                        .WithDoubleSide(true)
                        .WithChannelParam(KnownChannel.BaseColor, new Vector4(
                            (float)rhinoMaterial.DiffuseColor.R / 255,
                            (float)rhinoMaterial.DiffuseColor.G / 255,
                            (float)rhinoMaterial.DiffuseColor.B / 255,
                            1.0f - (float)rhinoMaterial.Transparency // 透明度の処理
                        ));
                    materialBuilders.Add(materialName, materialBuilder);
                }

                var meshBuilder = new MeshBuilder<VertexPositionNormal>("mesh");
                var prim = meshBuilder.UsePrimitive(materialBuilder);

                rhinoMesh.Faces.ConvertQuadsToTriangles(); // 四角形の面を三角形に変換
                rhinoMesh.Normals.ComputeNormals();
                rhinoMesh.Compact();

                foreach (var face in rhinoMesh.Faces)
                {
                    var vertexA = CreateVertexBuilder(rhinoMesh, face.A);
                    var vertexB = CreateVertexBuilder(rhinoMesh, face.B);
                    var vertexC = CreateVertexBuilder(rhinoMesh, face.C);

                    // 三角形をプリミティブに追加
                    prim.AddTriangle(vertexA, vertexB, vertexC);
                }
                
                var node = sceneBuilder.AddRigidMesh(meshBuilder, Matrix4x4.Identity);


                //Extrasへのユーザーデータの追加（必要に応じて）
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

            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> CreateVertexBuilder(RHINOMESH mesh, int vertexIndex)
            {
                var position = mesh.Vertices[vertexIndex];
                var normal = mesh.Normals[vertexIndex];
                return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(new VertexPositionNormal(
                   position.X, position.Z, -position.Y, normal.X, normal.Z, -normal.Y));
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
                model.SaveGLTF(filePath);
                Rhino.RhinoApp.WriteLine("GLTFファイルを " + filePath + " に保存しました。");
            }


            return Result.Success;
        }

        private static float GetModelScaleFactor(RhinoDoc doc)
        {
            var modelUnit = doc.ModelUnitSystem;
            var scale = 1.0f; // メートル単位への変換係数。デフォルトは1.0（メートルの場合）

            switch (modelUnit)
            {
                case UnitSystem.None:
                case UnitSystem.Meters:
                    scale = 1.0f;
                    break;
                case UnitSystem.Millimeters:
                    scale = 0.001f; // ミリメートルからメートルへ
                    break;
                case UnitSystem.Centimeters:
                    scale = 0.01f; // センチメートルからメートルへ
                    break;
                case UnitSystem.Inches:
                    scale = 0.0254f; // インチからメートルへ
                    break;
                case UnitSystem.Feet:
                    scale = 0.3048f; // フィートからメートルへ
                    break;
                    // その他の単位系の場合は、適宜変換係数を追加してください
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
