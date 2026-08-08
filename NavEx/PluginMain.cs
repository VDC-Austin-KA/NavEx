using System;
using System.Windows;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using NavEx.Core;
using NavApp = Autodesk.Navisworks.Api.Application;

namespace NavEx
{
    /// <summary>
    /// NavEx entry point — appears on the Add-Ins ribbon tab.
    ///
    /// Exports Navisworks search sets, selection sets and the current selection as
    /// lightweight glTF 2.0 (.glb / .gltf) or Wavefront OBJ, with materials embedded
    /// and geometry accurate to the source tessellation.
    /// </summary>
    [Plugin("NavEx",
        "ACLP_VDC",
        ToolTip = "NavEx: export search sets and selections as GLB / glTF / OBJ",
        DisplayName = "NavEx")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class PluginMain : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            try
            {
                Document document = NavApp.ActiveDocument;
                if (document == null || document.Models.Count == 0)
                {
                    MessageBox.Show(
                        "Open or append a model before exporting.",
                        "NavEx", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }

                var window = new MainWindow();

                // Parenting to the Navisworks main window keeps the dialog on top and
                // stops it being lost behind the application.
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(window);
                    helper.Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                }
                catch (Exception)
                {
                    // A parentless dialog still works; never block the export on this.
                }

                window.ShowDialog();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "NavEx failed to start:" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine + ex.StackTrace,
                    "NavEx", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        public override CommandState CanExecute()
        {
            return new CommandState(true);
        }
    }
}
