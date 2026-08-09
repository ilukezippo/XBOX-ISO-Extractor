using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace extract_xiso_gui
{
    public partial class About : Window
    {
        // Your fork — the GitHub logo opens this.
        // TODO: update to the exact repo URL once you publish it.
        public static string myGithubLink = "https://github.com/ilukezippo/XBOX-ISO-Extractor";

        // Original project this app is based on.
        public static string originalGithubLink = "https://github.com/KilLo445/extract-xiso-gui";

        public About()
        {
            InitializeComponent();
            VersionText.Text = "v" + MainWindow.guiVersion;

            if (File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "suppress-updates.txt"))) { cbEnableUpdates.IsChecked = false; }
            else { cbEnableUpdates.IsChecked = true; }
        }

        private void OpenGitHub(object sender, MouseButtonEventArgs e)
        {
            Process.Start(myGithubLink);
        }

        private void OpenOriginalGitHub(object sender, MouseButtonEventArgs e)
        {
            Process.Start(originalGithubLink);
        }

        private void cbEnableUpdates_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                File.Delete(Path.Combine(Directory.GetCurrentDirectory(), "suppress-updates.txt"));
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void cbEnableUpdates_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                // .Dispose() releases the file handle immediately.
                // The original code kept it open, locking the file until app exit.
                File.Create(Path.Combine(Directory.GetCurrentDirectory(), "suppress-updates.txt")).Dispose();
            }
            catch (Exception ex) { DisplayErrorMessage(ex); }
        }

        private void DisplayErrorMessage(Exception ex)
        {
            MessageBox.Show($"{ex}", "An error occured!", MessageBoxButton.OK, MessageBoxImage.Error);
            MessageBoxResult saveError = MessageBox.Show("Would you like to save the error to a file?", "Save error?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (saveError == MessageBoxResult.Yes) { SaveError(ex); }
            return;
        }
        private void SaveError(Exception ex)
        {
            string[] err ={
                            "An error occured!",
                            "XBOX ISO Extractor",
                            $"Version: {MainWindow.guiVersion}",
                            "",
                            $"{ex}"
                          };
            File.WriteAllLines(Path.Combine(Directory.GetCurrentDirectory(), "error.txt"), err);
            Process.Start(Path.Combine(Directory.GetCurrentDirectory(), "error.txt"));
        }
    }
}
