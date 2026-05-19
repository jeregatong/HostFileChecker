using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HostFileChecker
{
    public partial class Form1 : Form
    {
        private static readonly string HostsPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        private readonly BindingList<HostEntry> _entries = new BindingList<HostEntry>();
        private List<string> _rawLines = new List<string>();
        private bool _dirty;

        public Form1()
        {
            InitializeComponent();
            hostsGrid.DataSource = _entries;
        }

        // ---------- Model ----------

        private class HostEntry
        {
            public string IpAddress { get; set; }
            public string Hostname { get; set; }
            public string ResolvedIp { get; set; }
            public string Status { get; set; }
            public int OriginalLineIndex { get; set; } = -1;
            public bool Deleted { get; set; }
        }

        // ---------- Load ----------

        private void loadButton_Click(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(HostsPath))
                {
                    MessageBox.Show(this, "Hosts file not found at:\n" + HostsPath,
                        "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _rawLines = File.ReadAllLines(HostsPath).ToList();
                _entries.Clear();

                var entryRegex = new Regex(@"^\s*([0-9A-Fa-f\.:]+)\s+([^\s#]+)", RegexOptions.Compiled);

                for (int i = 0; i < _rawLines.Count; i++)
                {
                    string line = _rawLines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.TrimStart().StartsWith("#")) continue;

                    var match = entryRegex.Match(line);
                    if (!match.Success) continue;

                    _entries.Add(new HostEntry
                    {
                        IpAddress = match.Groups[1].Value,
                        Hostname = match.Groups[2].Value,
                        Status = "Not tested",
                        ResolvedIp = "",
                        OriginalLineIndex = i
                    });
                }

                _dirty = false;
                SetStatus(string.Format("Loaded {0} entries from {1}", _entries.Count, HostsPath));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load hosts file:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- Test (nslookup) ----------

        private async void testButton_Click(object sender, EventArgs e)
        {
            if (_entries.Count == 0)
            {
                MessageBox.Show(this, "Load the hosts file first.", "Nothing to test",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true, _entries.Count);

            try
            {
                int i = 0;
                int matches = 0, mismatches = 0, errors = 0;
                foreach (var entry in _entries.Where(x => !x.Deleted).ToList())
                {
                    SetStatus(string.Format("Testing {0} ({1}/{2})...", entry.Hostname, i + 1, _entries.Count));
                    statusProgress.Value = i;

                    var resolved = await NslookupAsync(entry.Hostname);
                    entry.ResolvedIp = resolved.Address ?? "";

                    if (resolved.Error != null)
                    {
                        entry.Status = "Error: " + resolved.Error;
                        errors++;
                    }
                    else if (string.IsNullOrEmpty(resolved.Address))
                    {
                        entry.Status = "Not resolved";
                        errors++;
                    }
                    else if (string.Equals(resolved.Address, entry.IpAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Status = "Match";
                        matches++;
                    }
                    else
                    {
                        entry.Status = "Mismatch";
                        mismatches++;
                    }

                    hostsGrid.Refresh();
                    i++;
                }

                SetStatus(string.Format("Test complete — {0} match, {1} mismatch, {2} error/unresolved.",
                    matches, mismatches, errors));
            }
            finally
            {
                SetBusy(false, 0);
            }
        }

        private async Task<(string Address, string Error)> NslookupAsync(string hostname)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo("nslookup", hostname)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using (var p = Process.Start(psi))
                    {
                        string stdout = p.StandardOutput.ReadToEnd();
                        string stderr = p.StandardError.ReadToEnd();
                        p.WaitForExit(8000);

                        if (Regex.IsMatch(stdout + stderr, @"can't find|Non-existent|NXDOMAIN", RegexOptions.IgnoreCase))
                            return (Address: (string)null, Error: "Not found");

                        // Skip the DNS server header — capture Address lines after "Name:"
                        var lines = stdout.Replace("\r", "").Split('\n');
                        bool pastNameMarker = false;
                        string firstAddress = null;
                        foreach (var raw in lines)
                        {
                            var trimmed = raw.Trim();
                            if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                            {
                                pastNameMarker = true;
                                continue;
                            }
                            if (!pastNameMarker) continue;

                            if (trimmed.StartsWith("Address:", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.StartsWith("Addresses:", StringComparison.OrdinalIgnoreCase))
                            {
                                var colon = trimmed.IndexOf(':');
                                var value = trimmed.Substring(colon + 1).Trim();
                                if (!string.IsNullOrEmpty(value) && firstAddress == null)
                                {
                                    firstAddress = value;
                                }
                            }
                            else if (pastNameMarker && Regex.IsMatch(trimmed, @"^[0-9A-Fa-f\.:]+$") && firstAddress != null)
                            {
                                // Additional address on continuation line — keep the first match.
                            }
                        }

                        if (firstAddress == null)
                            return (Address: (string)null, Error: "No address in output");

                        return (Address: firstAddress, Error: (string)null);
                    }
                }
                catch (Exception ex)
                {
                    return (Address: (string)null, Error: ex.Message);
                }
            });
        }

        // ---------- Add ----------

        private void addButton_Click(object sender, EventArgs e)
        {
            string ip = (ipTextBox.Text ?? "").Trim();
            string host = (hostTextBox.Text ?? "").Trim();

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(host))
            {
                MessageBox.Show(this, "Both IP address and hostname are required.",
                    "Missing input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!IPAddress.TryParse(ip, out _))
            {
                MessageBox.Show(this, "\"" + ip + "\" is not a valid IP address.",
                    "Invalid IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!IsLikelyHostname(host))
            {
                MessageBox.Show(this, "\"" + host + "\" is not a valid hostname.",
                    "Invalid hostname", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_entries.Any(x => !x.Deleted &&
                                  string.Equals(x.Hostname, host, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "An entry for \"" + host + "\" already exists.",
                    "Duplicate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _entries.Add(new HostEntry
            {
                IpAddress = ip,
                Hostname = host,
                Status = "New (unsaved)",
                ResolvedIp = "",
                OriginalLineIndex = -1
            });

            ipTextBox.Clear();
            hostTextBox.Clear();
            ipTextBox.Focus();
            _dirty = true;
            SetStatus("Added " + host + " — click \"Save Changes\" to write to hosts file.");
        }

        private static bool IsLikelyHostname(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 253) return false;
            return Regex.IsMatch(s, @"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*$");
        }

        // ---------- Delete (with typed-hostname confirmation) ----------

        private void hostsGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != colDelete.Index) return;
            if (e.RowIndex >= _entries.Count) return;

            var entry = _entries[e.RowIndex];
            if (entry.Deleted) return;

            string typed = PromptInput(
                "Confirm Deletion",
                "To delete this entry, type the hostname exactly:\n\n    " + entry.Hostname,
                "");

            if (typed == null) return; // cancelled

            if (!string.Equals(typed.Trim(), entry.Hostname, StringComparison.Ordinal))
            {
                MessageBox.Show(this,
                    "Hostname did not match. Deletion cancelled.",
                    "Not deleted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (entry.OriginalLineIndex == -1)
            {
                _entries.RemoveAt(e.RowIndex);
            }
            else
            {
                entry.Deleted = true;
                _tombstones.Add(entry);
                _entries.RemoveAt(e.RowIndex);
            }

            _dirty = true;
            SetStatus("Removed " + entry.Hostname + " — click \"Save Changes\" to update the hosts file.");
        }

        private readonly List<HostEntry> _tombstones = new List<HostEntry>();

        private string PromptInput(string title, string message, string defaultValue)
        {
            using (var dlg = new Form())
            {
                dlg.Text = title;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(440, 170);
                dlg.Font = new Font("Segoe UI", 9.5F);
                dlg.BackColor = Color.White;

                var lbl = new Label
                {
                    Text = message,
                    Location = new Point(16, 16),
                    Size = new Size(408, 60),
                    Font = new Font("Segoe UI", 9.5F),
                    ForeColor = Color.FromArgb(40, 50, 60)
                };

                var tb = new TextBox
                {
                    Text = defaultValue,
                    Location = new Point(16, 80),
                    Size = new Size(408, 25),
                    Font = new Font("Segoe UI", 10F),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var ok = new Button
                {
                    Text = "Delete",
                    DialogResult = DialogResult.OK,
                    Location = new Point(244, 120),
                    Size = new Size(90, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(220, 70, 70),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 9.5F)
                };
                ok.FlatAppearance.BorderSize = 0;

                var cancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(338, 120),
                    Size = new Size(86, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(230, 232, 236),
                    ForeColor = Color.FromArgb(40, 50, 60),
                    Font = new Font("Segoe UI", 9.5F)
                };
                cancel.FlatAppearance.BorderSize = 0;

                dlg.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                return dlg.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // ---------- Save ----------

        private void saveButton_Click(object sender, EventArgs e)
        {
            if (!_dirty && _tombstones.Count == 0)
            {
                MessageBox.Show(this, "No changes to save.", "Up to date",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this,
                    "Save changes to:\n" + HostsPath + "\n\nThis requires Administrator privileges.",
                    "Confirm Save", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            try
            {
                var deletedIndexes = new HashSet<int>(_tombstones
                    .Where(t => t.OriginalLineIndex >= 0)
                    .Select(t => t.OriginalLineIndex));

                var output = new List<string>();
                for (int i = 0; i < _rawLines.Count; i++)
                {
                    if (deletedIndexes.Contains(i)) continue;
                    output.Add(_rawLines[i]);
                }

                foreach (var newEntry in _entries.Where(x => x.OriginalLineIndex == -1 && !x.Deleted))
                {
                    output.Add(newEntry.IpAddress + "\t" + newEntry.Hostname);
                }

                File.WriteAllLines(HostsPath, output);

                _rawLines = output;
                _tombstones.Clear();
                foreach (var entry in _entries)
                {
                    if (entry.OriginalLineIndex == -1)
                    {
                        entry.OriginalLineIndex = output.FindIndex(l =>
                            l.IndexOf(entry.IpAddress, StringComparison.Ordinal) >= 0 &&
                            l.IndexOf(entry.Hostname, StringComparison.Ordinal) >= 0);
                        entry.Status = "Saved";
                    }
                }
                _dirty = false;
                hostsGrid.Refresh();
                SetStatus("Saved successfully to " + HostsPath);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(this,
                    "Access denied. Run this application as Administrator to modify the hosts file.",
                    "Permission required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save hosts file:\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- Grid formatting ----------

        private void hostsGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _entries.Count) return;
            if (e.ColumnIndex != colStatus.Index) return;

            var entry = _entries[e.RowIndex];
            if (entry.Status == null) return;

            if (entry.Status == "Match")
            {
                e.CellStyle.ForeColor = Color.FromArgb(20, 120, 60);
                e.CellStyle.Font = new Font(hostsGrid.Font, FontStyle.Bold);
            }
            else if (entry.Status == "Mismatch")
            {
                e.CellStyle.ForeColor = Color.FromArgb(200, 80, 30);
                e.CellStyle.Font = new Font(hostsGrid.Font, FontStyle.Bold);
            }
            else if (entry.Status.StartsWith("Error") || entry.Status == "Not resolved")
            {
                e.CellStyle.ForeColor = Color.FromArgb(190, 60, 60);
            }
            else if (entry.Status.StartsWith("New"))
            {
                e.CellStyle.ForeColor = Color.FromArgb(140, 90, 175);
                e.CellStyle.Font = new Font(hostsGrid.Font, FontStyle.Italic);
            }
            else
            {
                e.CellStyle.ForeColor = Color.FromArgb(110, 120, 130);
            }
        }

        // ---------- Helpers ----------

        private void SetStatus(string text)
        {
            statusLabel.Text = text;
        }

        private void SetBusy(bool busy, int max)
        {
            loadButton.Enabled = !busy;
            testButton.Enabled = !busy;
            saveButton.Enabled = !busy;
            addButton.Enabled = !busy;
            statusProgress.Visible = busy;
            statusProgress.Minimum = 0;
            statusProgress.Maximum = Math.Max(1, max);
            statusProgress.Value = 0;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
