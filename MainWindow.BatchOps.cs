using Microsoft.Win32;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Ookii.Dialogs.Wpf;

namespace AlphaPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Batch Operations (Multitask Tab)
        // ============================================================

        private void SelectBatchSplitCsv_Click(object sender, RoutedEventArgs e)
        {
            var csvDlg = new OpenFileDialog { Filter = "CSV files|*.csv|Text files|*.txt" };
            if (csvDlg.ShowDialog(this) == true) BatchSplitCsvPathBox.Text = csvDlg.FileName;
        }

        private async void StartBatchSplit_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null)
            {
                AppDialog.Show(this, Loc("Str_Batch_Err_NoDoc"));
                return;
            }

            string rangesText = BatchSplitRangesBox.Text.Trim();
            if (string.IsNullOrEmpty(rangesText))
            {
                AppDialog.Show(this, Loc("Str_Batch_Err_NoRanges"));
                return;
            }

            string csvPath = BatchSplitCsvPathBox.Text.Trim();
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
            {
                AppDialog.Show(this, Loc("Str_Batch_Err_NoCsv"));
                return;
            }

            var ranges = ParseRanges(rangesText, _doc.PageCount);
            if (ranges.Count == 0)
            {
                AppDialog.Show(this, Loc("Str_Batch_Err_InvalidRanges"));
                return;
            }

            var fileNames = ParseCsvNames(csvPath);
            if (fileNames.Count == 0) return;

            var folderDlg = new VistaFolderBrowserDialog { UseDescriptionForTitle = true };
            if (folderDlg.ShowDialog(this) != true) return;
            string outputDir = folderDlg.SelectedPath;

            string sourceFile = _currentFile;
            SetStatus(Loc("Str_Batch_SplitWorking"));

            try
            {
                int successCount = await Task.Run(() =>
                {
                    using var sourceDoc = PdfReader.Open(sourceFile, PdfDocumentOpenMode.Import);
                    int count = 0;

                    for (int i = 0; i < ranges.Count; i++)
                    {
                        var (startPg, endPg) = ranges[i];

                        string rawName = i < fileNames.Count ? fileNames[i] : $"Split_Part_{i + 1}.pdf";
                        if (!rawName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) rawName += ".pdf";
                        string cleanName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars()));

                        var newDoc = new PdfDocument();
                        for (int p = startPg - 1; p < endPg && p < sourceDoc.PageCount; p++)
                        {
                            newDoc.AddPage(sourceDoc.Pages[p]);
                        }

                        if (newDoc.PageCount > 0)
                        {
                            newDoc.Save(Path.Combine(outputDir, cleanName));
                            count++;
                        }
                        newDoc.Dispose();
                    }
                    return count;
                });

                SetStatus(string.Format(Loc("Str_Batch_SplitDone"), successCount));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_SplitSuccess"), successCount), "Batch Split", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                SetStatus(Loc("Str_Batch_SplitFailStatus"));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_SplitFail"), ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<(int start, int end)> ParseRanges(string input, int maxPages)
        {
            var list = new List<(int, int)>();
            input = input.Trim();

            if (input.EndsWith("~"))
            {
                string chunkStr = input.TrimEnd('~').Trim();
                if (int.TryParse(chunkStr, out int chunkSize) && chunkSize > 0)
                {
                    for (int i = 1; i <= maxPages; i += chunkSize)
                    {
                        list.Add((i, Math.Min(i + chunkSize - 1, maxPages)));
                    }
                    return list;
                }
            }

            var parts = input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var range = part.Trim();
                if (range.Contains('-'))
                {
                    var limits = range.Split('-');
                    if (limits.Length == 2 && int.TryParse(limits[0].Trim(), out int start) && int.TryParse(limits[1].Trim(), out int end))
                        if (start <= end && start > 0) list.Add((start, end));
                }
                else if (int.TryParse(range, out int singlePage) && singlePage > 0)
                {
                    list.Add((singlePage, singlePage));
                }
            }
            return list;
        }

        private List<string> ParseCsvNames(string csvPath)
        {
            var list = new List<string>();
            foreach (var line in File.ReadAllLines(csvPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string name = line.Split(',')[0].Trim(' ', '"', '\'', '\t');
                if (!string.IsNullOrEmpty(name) && !name.Equals("FileName", StringComparison.OrdinalIgnoreCase))
                    list.Add(name);
            }
            return list;
        }

        // ============================================================
        // 2. Batch Protect (Khóa file hàng loạt)
        // ============================================================

        private void SelectBatchProtectSource_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new VistaFolderBrowserDialog { UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == true) BatchProtectSourceBox.Text = dlg.SelectedPath;
        }

        private void SelectBatchProtectCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV files|*.csv|Text files|*.txt" };
            if (dlg.ShowDialog(this) == true) BatchProtectCsvBox.Text = dlg.FileName;
        }

        private async void StartBatchProtect_Click(object sender, RoutedEventArgs e)
        {
            string sourceDir = BatchProtectSourceBox.Text.Trim();
            string csvPath = BatchProtectCsvBox.Text.Trim();

            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) { AppDialog.Show(this, Loc("Str_Batch_Err_NoSourceFolder")); return; }
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath)) { AppDialog.Show(this, Loc("Str_Batch_Err_NoCsv")); return; }

            var outDlg = new VistaFolderBrowserDialog { UseDescriptionForTitle = true };
            if (outDlg.ShowDialog(this) != true) return;
            string outputDir = outDlg.SelectedPath;

            SetStatus(Loc("Str_Batch_ProtectWorking"));

            try
            {
                int successCount = await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(csvPath).Skip(1);
                    int count = 0;

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(',');
                        if (parts.Length < 2) continue;

                        string fileName = parts[0].Trim();
                        string password = parts[1].Trim();
                        string sourceFile = Path.Combine(sourceDir, fileName);

                        if (File.Exists(sourceFile))
                        {
                            string targetFile = Path.Combine(outputDir, fileName);
                            AlphaPDF.Services.PdfEncryptionService.Protect(sourceFile, targetFile, new AlphaPDF.Services.EncryptionOptions
                            {
                                UserPassword = password,
                                AllowPrint = true,
                                AllowCopy = false
                            });
                            count++;
                        }
                    }
                    return count;
                });

                SetStatus(string.Format(Loc("Str_Batch_ProtectDone"), successCount));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_ProtectSuccess"), successCount, outputDir), "Batch Protect", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                SetStatus(Loc("Str_Batch_ProtectFailStatus"));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_ProtectFail"), ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // 3. File Utilities: List to CSV & Batch Rename
        // ============================================================

        private void BatchListFiles_Click(object sender, RoutedEventArgs e)
        {
            var folderDlg = new VistaFolderBrowserDialog { UseDescriptionForTitle = true };
            if (folderDlg.ShowDialog(this) != true) return;

            var saveDlg = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = "FileList.csv" };
            if (saveDlg.ShowDialog(this) != true) return;

            try
            {
                var files = Directory.GetFiles(folderDlg.SelectedPath);
                var lines = new List<string> { "FileName,NewName(Optional)" };
                lines.AddRange(files.Select(f => Path.GetFileName(f) + ","));

                File.WriteAllLines(saveDlg.FileName, lines, System.Text.Encoding.UTF8);
                SetStatus(string.Format(Loc("Str_Batch_ListWorking"), files.Length));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_ListSuccess"), files.Length), "List to CSV", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, string.Format(Loc("Str_Batch_ListFail"), ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BatchRename_Click(object sender, RoutedEventArgs e)
        {
            var folderDlg = new VistaFolderBrowserDialog { UseDescriptionForTitle = true };
            if (folderDlg.ShowDialog(this) != true) return;

            var csvDlg = new OpenFileDialog { Filter = "CSV files|*.csv" };
            if (csvDlg.ShowDialog(this) != true) return;

            string folder = folderDlg.SelectedPath;
            string csvPath = csvDlg.FileName;

            SetStatus(Loc("Str_Batch_RenameWorking"));

            try
            {
                int successCount = await Task.Run(() =>
                {
                    var lines = File.ReadAllLines(csvPath).Skip(1);
                    int count = 0;

                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var parts = line.Split(',');
                        if (parts.Length < 2) continue;

                        string oldName = parts[0].Trim(' ', '"', '\'');
                        string newName = parts[1].Trim(' ', '"', '\'');

                        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName) continue;

                        string oldPath = Path.Combine(folder, oldName);
                        string newPath = Path.Combine(folder, newName);

                        if (File.Exists(oldPath) && !File.Exists(newPath))
                        {
                            File.Move(oldPath, newPath);
                            count++;
                        }
                    }
                    return count;
                });

                SetStatus(string.Format(Loc("Str_Batch_RenameDone"), successCount));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_RenameSuccess"), successCount), "Batch Rename", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                SetStatus(Loc("Str_Batch_RenameFailStatus"));
                AppDialog.Show(this, string.Format(Loc("Str_Batch_RenameFail"), ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
