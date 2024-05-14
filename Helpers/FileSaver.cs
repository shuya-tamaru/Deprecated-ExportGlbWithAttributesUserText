using System.IO;
using SYSENV = System.Environment;
using Rhino;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace ExportGlb.Helpers
{
    public static class FileSaver
    {
        public static bool ShowSaveFileDialogAndSave(SceneBuilder sceneBuilder, RhinoDoc doc)
        {
            var docPath = doc.Path;
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
                return false;
            }


            var filePath = saveFileDialog.FileName;

            SaveFile(sceneBuilder, filePath);

            return true;
        }

        private static void SaveFile(SceneBuilder sceneBuilder, string filePath)
        {
            var fileFormat = Path.GetExtension(filePath).ToLower();
            var model = sceneBuilder.ToGltf2();
            if (fileFormat == ".glb")
            {                
                 model.SaveGLB(filePath);
                 Rhino.RhinoApp.WriteLine("GLB Exported to " + filePath);
            }
            else if (fileFormat == ".gltf")
            {
                var writeSettings = new WriteSettings
                {
                    JsonIndented = true,
                    MergeBuffers = true
                };
                model.SaveGLTF(filePath, writeSettings);
                Rhino.RhinoApp.WriteLine("GLTF Exported to " + filePath);
            }
        }

    }
}