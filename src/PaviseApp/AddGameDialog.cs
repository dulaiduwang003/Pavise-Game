using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class AddGameDialog : Form
	{
		private enum RowKind
		{
			Installed,
			Running,
			Picked
		}

		private sealed class Candidate
		{
			public string Name;

			public string Path;

			public string Root;

			public long Memory;

			public int Count;

			public bool Pick;

			public bool NeedsChoice;
		}

		private class Row
		{
			public string Name;

			public string Path;

			public string Root;

			public bool Checked;

			public bool Already;

			public bool Installed;

			public bool NeedsChoice;

			public bool Running;

			public bool Pinned;

			public long Memory;

			public int Count;

			public double Gpu;

			public bool RendererLike;
		}

		private struct RowLayout
		{
			public Rectangle Check;

			public Rectangle Icon;

			public Rectangle Name;

			public Rectangle Path;

			public Rectangle Status;

			public Rectangle Details;
		}

		private const int FolderCandidateCap = 24;

		public readonly List<ScanHit> Selected = new List<ScanHit>();

		private readonly bool allowGpuProbe;

		private readonly HashSet<string> existing;

		private readonly List<Row> rows = new List<Row>();

		private readonly List<Row> shown = new List<Row>();

		private readonly object iconGate = new object();

		private readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

		private readonly HashSet<string> queuedIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private ListBox lst;

		private TextBox tbFilter;

		private Label lblInfo;

		private PillButton btnAdd;

		private PillButton btnAll;

		private PillButton btnBrowse;

		private PillButton btnFolder;

		private readonly List<string> seedPaths = new List<string>();

		private int pickBusy;

		private volatile bool closed;

		private volatile bool scanning;

		private volatile bool collectingRunning;

		private volatile bool probingGpu;

		private volatile bool refreshBusy;

		private int dots;

		private System.Windows.Forms.Timer infoTimer;

		private System.Windows.Forms.Timer runningTimer;

		private int hover = -1;

		private bool selectionEdited;

		protected override CreateParams CreateParams
		{
			get
			{
				CreateParams createParams = base.CreateParams;
				createParams.ClassStyle |= 131072;
				return createParams;
			}
		}

		private bool Busy
		{
			get
			{
				if (!collectingRunning && !probingGpu)
				{
					return scanning;
				}
				return true;
			}
		}

		public AddGameDialog(IEnumerable<string> alreadyInLibrary, bool allowGpuProbe)
			: this(alreadyInLibrary, allowGpuProbe, null)
		{
		}

		public AddGameDialog(IEnumerable<string> alreadyInLibrary, bool allowGpuProbe, IEnumerable<string> seeds)
		{
			this.allowGpuProbe = allowGpuProbe;
			if (seeds != null)
			{
				foreach (string seed in seeds)
				{
					if (!string.IsNullOrEmpty(seed))
					{
						seedPaths.Add(seed);
					}
				}
			}
			existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (alreadyInLibrary != null)
			{
				foreach (string item in alreadyInLibrary)
				{
					if (!string.IsNullOrEmpty(item))
					{
						existing.Add(item);
					}
				}
			}
			base.FormBorderStyle = FormBorderStyle.None;
			base.StartPosition = FormStartPosition.CenterParent;
			base.ShowInTaskbar = false;
			base.ClientSize = new Size(Theme.S(620), Theme.S(560));
			BackColor = Theme.Bg;
			Font = Theme.UI(9.5f, false);
			Label label = new Label();
			label.Text = Lang.T("scan.title");
			label.ForeColor = Theme.Fg;
			label.Font = Theme.UI(10.5f, true);
			label.SetBounds(Theme.S(16), Theme.S(12), Theme.S(300), Theme.S(24));
			label.MouseDown += DragMove;
			Label label2 = new Label();
			label2.Text = Lang.T("scan.hint");
			label2.ForeColor = Theme.Dim;
			label2.BackColor = Theme.Bg;
			label2.Font = Theme.UI(8.25f, false);
			label2.SetBounds(Theme.S(16), Theme.S(42), Theme.S(588), Theme.S(40));
			Label label3 = new Label();
			label3.Text = "✕";
			label3.ForeColor = Theme.Dim;
			label3.SetBounds(Theme.S(584), Theme.S(10), Theme.S(26), Theme.S(26));
			label3.TextAlign = ContentAlignment.MiddleCenter;
			label3.Cursor = Cursors.Hand;
			label3.Click += delegate
			{
				base.DialogResult = DialogResult.Cancel;
			};
			tbFilter = Theme.MakeTextBox(Theme.S(16), Theme.S(88), Theme.S(488));
			tbFilter.TextChanged += delegate
			{
				Refill();
			};
			btnAll = new PillButton(Lang.T("scan.all"));
			btnAll.SetBounds(Theme.S(512), Theme.S(88), Theme.S(92), Theme.S(30));
			btnAll.Click += delegate
			{
				ToggleAll();
			};
			RoundPanel roundPanel = new RoundPanel();
			roundPanel.SetBounds(Theme.S(16), Theme.S(128), Theme.S(588), Theme.S(348));
			roundPanel.BackColor = Theme.Bg;
			roundPanel.Fill = Theme.Card;
			roundPanel.Border = Theme.Stroke;
			roundPanel.Radius = Theme.S(12);
			roundPanel.Padding = new Padding(Theme.S(6));
			lst = new TechListBox();
			lst.Dock = DockStyle.Fill;
			lst.BackColor = Theme.Card;
			lst.ForeColor = Theme.Fg;
			lst.BorderStyle = BorderStyle.None;
			lst.DrawMode = DrawMode.OwnerDrawFixed;
			lst.ItemHeight = Math.Min(255, Theme.S(52));
			lst.IntegralHeight = false;
			lst.Font = Theme.UI(9.5f, false);
			lst.DrawItem += DrawRow;
			lst.MouseMove += delegate(object s, MouseEventArgs e)
			{
				int indexFromPointResult = lst.IndexFromPoint(e.Location);
				if (indexFromPointResult != hover)
				{
					int index2 = hover;
					hover = indexFromPointResult;
					InvalidateRow(index2);
					InvalidateRow(indexFromPointResult);
				}
			};
			lst.MouseLeave += delegate
			{
				if (hover >= 0)
				{
					int index = hover;
					hover = -1;
					InvalidateRow(index);
				}
			};
			lst.MouseClick += delegate(object s, MouseEventArgs e)
			{
				if (e.Button == MouseButtons.Left)
				{
					int indexFromPointResult = lst.IndexFromPoint(e.Location);
					if (indexFromPointResult >= 0)
					{
						ToggleAt(indexFromPointResult);
					}
				}
			};
			lst.DoubleClick += delegate
			{
				int selectedIndex = lst.SelectedIndex;
				if (selectedIndex >= 0 && selectedIndex < shown.Count)
				{
					Row row = shown[selectedIndex];
					if (!row.Already)
					{
						selectionEdited = true;
						row.Checked = true;
						Accept();
					}
				}
			};
			roundPanel.Controls.Add(new TechListScrollHost((TechListBox)lst)
			{
				Dock = DockStyle.Fill
			});
			lblInfo = new Label();
			lblInfo.Text = Lang.T("scan.busy");
			lblInfo.ForeColor = Theme.Dim;
			lblInfo.BackColor = Theme.Bg;
			lblInfo.Font = Theme.UI(8.25f, false);
			lblInfo.TextAlign = ContentAlignment.MiddleLeft;
			lblInfo.SetBounds(Theme.S(16), Theme.S(480), Theme.S(588), Theme.S(20));
			lblInfo.AutoEllipsis = true;
			btnBrowse = new PillButton(Lang.T("scan.browse"));
			btnBrowse.SetBounds(Theme.S(16), Theme.S(506), Theme.S(122), Theme.S(34));
			btnBrowse.Click += delegate
			{
				BrowseFile();
			};
			btnFolder = new PillButton(Lang.T("scan.folder"));
			btnFolder.SetBounds(Theme.S(146), Theme.S(506), Theme.S(122), Theme.S(34));
			btnFolder.Click += delegate
			{
				BrowseFolder();
			};
			btnAdd = new PillButton(Lang.T("btn.add"), BtnKind.Primary);
			btnAdd.Enabled = false;
			btnAdd.SetBounds(Theme.S(392), Theme.S(506), Theme.S(110), Theme.S(34));
			btnAdd.Click += delegate
			{
				Accept();
			};
			PillButton pillButton = new PillButton(Lang.T("btn.cancel"));
			pillButton.SetBounds(Theme.S(514), Theme.S(506), Theme.S(90), Theme.S(34));
			pillButton.Click += delegate
			{
				base.DialogResult = DialogResult.Cancel;
			};
			base.Controls.AddRange(new Control[11]
			{
				label, label2, label3, tbFilter, btnAll, roundPanel, lblInfo, btnBrowse, btnFolder, btnAdd,
				pillButton
			});
			base.Load += delegate
			{
				StartTimers();
				StartRunningCollect(true);
				StartScan();
				if (seedPaths.Count > 0)
				{
					AddDroppedPaths(seedPaths.ToArray());
				}
			};
			base.MouseDown += DragMove;
			base.KeyPreview = true;
			base.KeyDown += delegate(object s, KeyEventArgs e)
			{
				if (e.KeyCode == Keys.Escape)
				{
					base.DialogResult = DialogResult.Cancel;
				}
				else if (e.KeyCode == Keys.Space && !tbFilter.Focused)
				{
					ToggleSelected();
					e.SuppressKeyPress = true;
				}
				else if (e.KeyCode == Keys.Return && !tbFilter.Focused)
				{
					Accept();
					e.SuppressKeyPress = true;
				}
			};
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			using (Pen pen = new Pen(Theme.Accent))
			{
				e.Graphics.DrawRectangle(pen, 0, 0, base.Width - 1, base.Height - 1);
			}
		}

		protected override void OnHandleCreated(EventArgs e)
		{
			base.OnHandleCreated(e);
			Native.RoundCorners(base.Handle);
			Native.EnableElevatedFileDrop(base.Handle);
		}

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == 563)
			{
				AddDroppedPaths(Native.ReadDroppedFiles(m.WParam));
				m.Result = IntPtr.Zero;
			}
			else
			{
				base.WndProc(ref m);
			}
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			Fx.EnterForm(this);
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			StopWork();
			base.OnFormClosed(e);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				StopWork();
			}
			base.Dispose(disposing);
		}

		private void StopWork()
		{
			closed = true;
			StopTimers();
			lock (iconGate)
			{
				foreach (Bitmap value in iconCache.Values)
				{
					if (value != null)
					{
						value.Dispose();
					}
				}
				iconCache.Clear();
				queuedIcons.Clear();
			}
		}

		private void DragMove(object s, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				Native.ReleaseCapture();
				Native.SendMessage(base.Handle, 161, (IntPtr)2, IntPtr.Zero);
			}
		}

		private void StartTimers()
		{
			if (closed || infoTimer != null)
			{
				return;
			}
			infoTimer = new System.Windows.Forms.Timer();
			infoTimer.Interval = 420;
			infoTimer.Tick += delegate
			{
				if (Busy)
				{
					dots = (dots + 1) % 4;
					UpdateInfoLabel();
				}
			};
			infoTimer.Start();
			runningTimer = new System.Windows.Forms.Timer();
			runningTimer.Interval = 3000;
			runningTimer.Tick += delegate
			{
				StartRunningCollect(false);
			};
			runningTimer.Start();
		}

		private void StopTimers()
		{
			try
			{
				if (infoTimer != null)
				{
					infoTimer.Stop();
					infoTimer.Dispose();
					infoTimer = null;
				}
			}
			catch
			{
			}
			try
			{
				if (runningTimer != null)
				{
					runningTimer.Stop();
					runningTimer.Dispose();
					runningTimer = null;
				}
			}
			catch
			{
			}
		}

		private void StartRunningCollect(bool first)
		{
			if (closed || refreshBusy || collectingRunning)
			{
				return;
			}
			if (first)
			{
				collectingRunning = true;
			}
			else
			{
				refreshBusy = true;
			}
			bool probe = first && allowGpuProbe;
			if (first)
			{
				UpdateInfoLabel();
			}
			Thread thread = new Thread((ThreadStart)delegate
			{
				List<Candidate> hits = null;
				Dictionary<string, List<int>> dictionary = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
				try
				{
					List<RunningProgram> list = RunningProgramDiscovery.Scan(IsClosed);
					if (list != null)
					{
						hits = new List<Candidate>();
						foreach (RunningProgram item in list)
						{
							if (closed)
							{
								return;
							}
							Candidate candidate = ResolveCandidate(item.Path, item.Title, null);
							if (candidate != null)
							{
								candidate.Memory = item.Memory;
								candidate.Count = item.ProcessIds.Count;
								hits.Add(candidate);
								dictionary[candidate.Path] = item.ProcessIds;
							}
						}
					}
				}
				catch
				{
					hits = null;
				}
				if (!closed)
				{
					bool sampling = probe && hits != null && dictionary.Count > 0;
					Post(delegate
					{
						collectingRunning = false;
						refreshBusy = false;
						if (sampling)
						{
							probingGpu = true;
						}
						Merge(hits, RowKind.Running);
					});
					if (sampling)
					{
						Dictionary<int, double> dictionary2 = null;
						try
						{
							dictionary2 = GpuEvidence.Sample3D(2, 700, IsClosed);
						}
						catch
						{
						}
						Dictionary<string, double> utilByPath = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
						if (dictionary2 != null)
						{
							foreach (KeyValuePair<string, List<int>> item2 in dictionary)
							{
								double measuredValue = 0.0;
								foreach (int item3 in item2.Value)
								{
									double value;
									if (dictionary2.TryGetValue(item3, out value))
									{
										measuredValue = Math.Max(measuredValue, value);
									}
								}
								if (measuredValue > 0.0)
								{
									utilByPath[item2.Key] = measuredValue;
								}
							}
						}
						Post(delegate
						{
							probingGpu = false;
							if (utilByPath.Count > 0)
							{
								ApplyGpuTags(utilByPath);
							}
							else
							{
								UpdateInfoLabel();
							}
						});
					}
				}
			});
			thread.IsBackground = true;
			thread.Start();
		}

		private void UpdateInfoLabel()
		{
			string scanStageRunningText = null;
			if (collectingRunning)
			{
				scanStageRunningText = Lang.T("scan.stage.running");
			}
			else if (probingGpu)
			{
				scanStageRunningText = Lang.T("scan.stage.gpu");
			}
			else if (scanning)
			{
				scanStageRunningText = Lang.T("scan.busy");
			}
			if (scanStageRunningText != null)
			{
				string message = new string('·', dots);
				lblInfo.ForeColor = Theme.Accent;
				lblInfo.Text = ((rows.Count == 0) ? (scanStageRunningText + message) : Lang.F("scan.stage.count", scanStageRunningText + message, rows.Count));
				return;
			}
			int processedCount = 0;
			foreach (Row row in rows)
			{
				if (!row.Already)
				{
					processedCount++;
				}
			}
			lblInfo.ForeColor = Theme.Dim;
			lblInfo.Text = ((rows.Count == 0) ? Lang.T("scan.none") : (Lang.F("scan.count", rows.Count, processedCount) + "   " + Lang.T("scan.live")));
		}

		private void ApplyGpuTags(Dictionary<string, double> utilByPath)
		{
			Row row = null;
			foreach (Row row2 in rows)
			{
				row2.RendererLike = false;
				double value;
				if (row2.Running && utilByPath.TryGetValue(row2.Path, out value))
				{
					row2.Gpu = value;
					if (row == null || value > row.Gpu)
					{
						row = row2;
					}
				}
			}
			if (row != null && row.Gpu >= 10.0)
			{
				row.RendererLike = true;
				if (!selectionEdited && !row.Already)
				{
					row.Checked = true;
				}
			}
			SortRows();
			Refill();
			UpdateInfoLabel();
		}

		private bool IsClosed()
		{
			return closed;
		}

		private void StartScan()
		{
			if (closed || scanning)
			{
				return;
			}
			scanning = true;
			UpdateInfoLabel();
			Thread thread = new Thread((ThreadStart)delegate
			{
				List<Candidate> hits = new List<Candidate>();
				try
				{
					foreach (ScanHit item in GameScan.RunManifests(IsClosed))
					{
						if (closed)
						{
							return;
						}
						if (item != null && !string.IsNullOrEmpty(item.Root))
						{
							Candidate candidate = ResolveCandidate(item.Exe, item.Name, item.Root);
							if (candidate != null)
							{
								candidate.NeedsChoice = item.NeedsChoice;
								hits.Add(candidate);
							}
						}
					}
				}
				catch
				{
				}
				Post(delegate
				{
					scanning = false;
					Merge(hits, RowKind.Installed);
				});
			});
			thread.IsBackground = true;
			thread.Start();
		}

		private void BrowseFile()
		{
			using (OpenFileDialog openFileDialog = new OpenFileDialog())
			{
				openFileDialog.Title = Lang.T("ofd.game");
				openFileDialog.Filter = Lang.T("ofd.filter");
				openFileDialog.CheckFileExists = false;
				openFileDialog.DereferenceLinks = false;
				if (openFileDialog.ShowDialog(this) != DialogResult.OK)
				{
					return;
				}
				string executablePath;
				string error;
				if (!GameExecutableResolver.TryResolve(openFileDialog.FileName, out executablePath, out error))
				{
					if (!string.IsNullOrEmpty(error))
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", error);
					}
					return;
				}
				Selected.Clear();
				Selected.Add(new ScanHit
				{
					Name = null,
					Root = GameScan.InferGameRoot(executablePath),
					Exe = executablePath
				});
				base.DialogResult = DialogResult.OK;
			}
		}

		private void BrowseFolder()
		{
			using (FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog())
			{
				folderBrowserDialog.Description = Lang.T("fbd.game");
				folderBrowserDialog.ShowNewFolderButton = false;
				if (folderBrowserDialog.ShowDialog(this) == DialogResult.OK)
				{
					AddDroppedPaths(new string[1] { folderBrowserDialog.SelectedPath });
				}
			}
		}

		private void AddDroppedPaths(string[] paths)
		{
			if (closed || paths == null || paths.Length == 0)
			{
				return;
			}
			List<string> files = new List<string>();
			List<string> folders = new List<string>();
			foreach (string entryPath in paths)
			{
				if (!string.IsNullOrWhiteSpace(entryPath))
				{
					string trimTrimmed = entryPath.Trim().Trim('"');
					if (Directory.Exists(trimTrimmed))
					{
						folders.Add(trimTrimmed);
					}
					else
					{
						files.Add(trimTrimmed);
					}
				}
			}
			if ((files.Count == 0 && folders.Count == 0) || Interlocked.CompareExchange(ref pickBusy, 1, 0) != 0)
			{
				return;
			}
			Thread thread = new Thread((ThreadStart)delegate
			{
				List<Candidate> hits = new List<Candidate>();
				List<string> emptyFolders = new List<string>();
				string firstError = null;
				try
				{
					foreach (string item in files)
					{
						if (closed)
						{
							return;
						}
						string executablePath;
						string error;
						if (!GameExecutableResolver.TryResolve(item, out executablePath, out error))
						{
							if (firstError == null && !string.IsNullOrEmpty(error))
							{
								firstError = error;
							}
						}
						else
						{
							hits.Add(new Candidate
							{
								Name = Path.GetFileNameWithoutExtension(executablePath),
								Path = executablePath,
								Root = GameScan.InferGameRoot(executablePath),
								Pick = true
							});
						}
					}
					foreach (string item2 in folders)
					{
						if (closed)
						{
							return;
						}
						string fileName = Path.GetFileName(item2.TrimEnd('\\', '/'));
						string recommended;
						List<ExecutableCandidateFacts> list = ExecutableCandidateProbe.ListCandidates(item2, 24, out recommended);
						if (recommended != null)
						{
							hits.Add(new Candidate
							{
								Name = fileName,
								Path = recommended,
								Root = item2,
								Pick = true
							});
						}
						else if (list.Count == 0)
						{
							emptyFolders.Add(item2);
						}
						else
						{
							foreach (ExecutableCandidateFacts item3 in list)
							{
								hits.Add(new Candidate
								{
									Name = Path.GetFileNameWithoutExtension(item3.Path),
									Path = item3.Path,
									Root = item2,
									NeedsChoice = true,
									Pick = false
								});
							}
						}
					}
				}
				catch
				{
				}
				finally
				{
					Interlocked.Exchange(ref pickBusy, 0);
				}
				if (!closed)
				{
					Post(delegate
					{
						Merge(hits, RowKind.Picked);
						if (emptyFolders.Count > 0)
						{
							PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("scan.folder.none") + "\r\n" + string.Join("\r\n", emptyFolders.ToArray()));
						}
						else if (firstError != null && hits.Count == 0)
						{
							PaviseDialog.Warn(this, "PAVISE AUTO", firstError);
						}
					});
				}
			});
			thread.IsBackground = true;
			thread.Start();
		}

		private void Merge(List<Candidate> hits, RowKind kind)
		{
			if (closed || hits == null)
			{
				if (!closed)
				{
					UpdateInfoLabel();
				}
				return;
			}
			Dictionary<string, Row> dictionary = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
			foreach (Row row3 in rows)
			{
				dictionary[row3.Path] = row3;
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool mergeReady = false;
			foreach (Candidate hit in hits)
			{
				if (hit == null || string.IsNullOrEmpty(hit.Path) || (kind == RowKind.Installed && string.IsNullOrEmpty(hit.Root)))
				{
					continue;
				}
				Row value;
				if (!dictionary.TryGetValue(hit.Path, out value))
				{
					Row row = new Row();
					row.Path = hit.Path;
					row.Already = existing.Contains(hit.Path);
					value = row;
					rows.Add(value);
					dictionary[value.Path] = value;
					mergeReady = true;
				}
				int rowGroupResult = RowGroup(value);
				string name = value.Name;
				if (kind == RowKind.Installed || !value.Installed)
				{
					value.Name = (string.IsNullOrEmpty(hit.Name) ? Path.GetFileNameWithoutExtension(hit.Path) : hit.Name);
					value.Root = hit.Root;
					if (value.NeedsChoice != hit.NeedsChoice)
					{
						mergeReady = true;
					}
					value.NeedsChoice = hit.NeedsChoice;
				}
				switch (kind)
				{
				case RowKind.Installed:
					value.Installed = true;
					break;
				case RowKind.Picked:
					value.Pinned = true;
					if (hit.Pick && !value.Already && !value.Checked)
					{
						value.Checked = true;
						selectionEdited = true;
					}
					break;
				default:
					value.Running = true;
					value.Memory = hit.Memory;
					value.Count = hit.Count;
					hashSet.Add(value.Path);
					break;
				}
				if (rowGroupResult != RowGroup(value) || name != value.Name)
				{
					mergeReady = true;
				}
			}
			if (kind == RowKind.Running)
			{
				for (int rowsIndex = rows.Count - 1; rowsIndex >= 0; rowsIndex--)
				{
					Row row2 = rows[rowsIndex];
					if (!hashSet.Contains(row2.Path))
					{
						if (row2.Running)
						{
							row2.Running = false;
							row2.Memory = 0L;
							row2.Count = 0;
							row2.Gpu = 0.0;
							row2.RendererLike = false;
							mergeReady = true;
						}
						if (!row2.Installed && !row2.Checked && !row2.Pinned)
						{
							rows.RemoveAt(rowsIndex);
							mergeReady = true;
						}
					}
				}
			}
			if (mergeReady)
			{
				SortRows();
				Refill();
			}
			else
			{
				lst.Invalidate();
			}
			QueueIcons();
			UpdateInfoLabel();
		}

		private void SortRows()
		{
			rows.Sort(delegate(Row a, Row b)
			{
				int rowGroupResult = RowGroup(a);
				int rowGroupResult2 = RowGroup(b);
				if (rowGroupResult != rowGroupResult2)
				{
					return rowGroupResult - rowGroupResult2;
				}
				int compareResult = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
				return (compareResult == 0) ? string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) : compareResult;
			});
		}

		private static int RowGroup(Row r)
		{
			if (r.Already)
			{
				return 3;
			}
			if (r.Pinned)
			{
				return -1;
			}
			if (r.RendererLike)
			{
				return 0;
			}
			if (!r.Running)
			{
				return 2;
			}
			return 1;
		}

		private static Candidate ResolveCandidate(string path, string name, string root)
		{
			string executablePath;
			string error;
			if (!GameExecutableResolver.TryResolve(path, out executablePath, out error))
			{
				return null;
			}
			Candidate candidate = new Candidate();
			candidate.Name = (string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(executablePath) : name);
			candidate.Path = executablePath;
			candidate.Root = (string.IsNullOrEmpty(root) ? GameScan.InferGameRoot(executablePath) : root);
			return candidate;
		}

		private void Refill()
		{
			string query = tbFilter.Text.Trim();
			int selectedIndex = lst.SelectedIndex;
			Row row = ((selectedIndex >= 0 && selectedIndex < shown.Count) ? shown[selectedIndex] : null);
			int topIndex = lst.TopIndex;
			Row row2 = ((topIndex >= 0 && topIndex < shown.Count) ? shown[topIndex] : null);
			lst.BeginUpdate();
			try
			{
				lst.Items.Clear();
				shown.Clear();
				foreach (Row row3 in rows)
				{
					if (GameSearchMatcher.Matches(query, row3.Name, row3.Path))
					{
						shown.Add(row3);
						lst.Items.Add(DisplayRowName(row3));
					}
				}
				if (row != null)
				{
					lst.SelectedIndex = shown.IndexOf(row);
				}
				int matchIndex = ((row2 == null) ? (-1) : shown.IndexOf(row2));
				if (shown.Count > 0)
				{
					lst.TopIndex = ((matchIndex >= 0) ? matchIndex : Math.Min(Math.Max(0, topIndex), shown.Count - 1));
				}
				hover = -1;
			}
			finally
			{
				lst.EndUpdate();
			}
			UpdateAddButton();
		}

		private void ToggleSelected()
		{
			ToggleAt(lst.SelectedIndex);
		}

		private static string DisplayRowName(Row row)
		{
			if (!row.NeedsChoice || !row.Installed)
			{
				return row.Name;
			}
			return row.Name + " · " + Path.GetFileName(row.Path);
		}

		private void ToggleAt(int i)
		{
			if (i >= 0 && i < shown.Count)
			{
				Row row = shown[i];
				if (!row.Already)
				{
					selectionEdited = true;
					row.Checked = !row.Checked;
					lst.Invalidate();
					UpdateAddButton();
				}
			}
		}

		private void ToggleAll()
		{
			selectionEdited = true;
			bool checkedValue = false;
			foreach (Row item in shown)
			{
				if (!item.Already && !item.NeedsChoice && !item.Checked)
				{
					checkedValue = true;
					break;
				}
			}
			foreach (Row item2 in shown)
			{
				if (!item2.Already && (!checkedValue || !item2.NeedsChoice))
				{
					item2.Checked = checkedValue;
				}
			}
			lst.Invalidate();
			UpdateAddButton();
		}

		private void UpdateAddButton()
		{
			int processedCount = 0;
			foreach (Row row in rows)
			{
				if (row.Checked && !row.Already)
				{
					processedCount++;
				}
			}
			btnAdd.Text = ((processedCount > 0) ? Lang.F("scan.add.n", processedCount) : Lang.T("btn.add"));
			btnAdd.Enabled = processedCount > 0;
			btnAdd.Invalidate();
		}

		private void InvalidateRow(int index)
		{
			if (index >= 0 && index < lst.Items.Count)
			{
				lst.Invalidate(lst.GetItemRectangle(index));
			}
		}

		private void QueueIcons()
		{
			if (closed || !base.IsHandleCreated)
			{
				return;
			}
			List<string> paths = new List<string>();
			lock (iconGate)
			{
				foreach (Row row in rows)
				{
					if (!iconCache.ContainsKey(row.Path) && queuedIcons.Add(row.Path))
					{
						paths.Add(row.Path);
					}
				}
			}
			if (paths.Count == 0)
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				foreach (string item in paths)
				{
					if (closed)
					{
						break;
					}
					Bitmap bitmap = null;
					try
					{
						using (Icon icon = Icon.ExtractAssociatedIcon(item))
						{
							if (icon != null)
							{
								bitmap = icon.ToBitmap();
							}
						}
					}
					catch
					{
					}
					CacheIcon(item, bitmap);
				}
			});
		}

		private void CacheIcon(string path, Bitmap bitmap)
		{
			lock (iconGate)
			{
				queuedIcons.Remove(path);
				if (closed)
				{
					if (bitmap != null)
					{
						bitmap.Dispose();
					}
					return;
				}
				Bitmap value;
				if (iconCache.TryGetValue(path, out value) && value != null)
				{
					value.Dispose();
				}
				iconCache[path] = bitmap;
			}
			Post(delegate
			{
				for (int i = 0; i < shown.Count; i++)
				{
					if (string.Equals(shown[i].Path, path, StringComparison.OrdinalIgnoreCase))
					{
						InvalidateRow(i);
					}
				}
			});
		}

		private static RowLayout LayoutRow(Rectangle bounds, string status, string details)
		{
			RowLayout result = default(RowLayout);
			int scaledDimension = Theme.S(16);
			int scaledDimension2 = Theme.S(26);
			int scaledDimension3 = Theme.S(12);
			result.Check = new Rectangle(bounds.Left + Theme.S(14), bounds.Top + (bounds.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			result.Icon = new Rectangle(result.Check.Right + scaledDimension3, bounds.Top + (bounds.Height - scaledDimension2) / 2, scaledDimension2, scaledDimension2);
			int rightOffset = result.Icon.Right + scaledDimension3;
			int remainingRight = bounds.Right - Theme.S(14);
			TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
			int width = ((!string.IsNullOrEmpty(status)) ? TextRenderer.MeasureText(status, Theme.UI(7.6f, true), new Size(int.MaxValue, int.MaxValue), flags).Width : 0);
			int width2 = ((!string.IsNullOrEmpty(details)) ? TextRenderer.MeasureText(details, Theme.UI(7.6f, false), new Size(int.MaxValue, int.MaxValue), flags).Width : 0);
			int maxLimit = Math.Min(Math.Max(width, width2) + Theme.S(2), Math.Max(0, (remainingRight - rightOffset) / 2));
			if (width == 0 && width2 == 0)
			{
				maxLimit = 0;
			}
			int lowerBound = Math.Max(1, remainingRight - rightOffset - ((maxLimit > 0) ? (maxLimit + scaledDimension3) : 0));
			result.Name = new Rectangle(rightOffset, bounds.Top + Theme.S(6), lowerBound, Theme.S(20));
			result.Path = new Rectangle(rightOffset, bounds.Top + Theme.S(28), lowerBound, Theme.S(18));
			result.Status = new Rectangle(remainingRight - maxLimit, result.Name.Top, maxLimit, result.Name.Height);
			result.Details = new Rectangle(remainingRight - maxLimit, result.Path.Top, maxLimit, result.Path.Height);
			return result;
		}

		private void DrawRow(object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || e.Index >= shown.Count)
			{
				return;
			}
			Row row = shown[e.Index];
			bool valuesMatch = (e.State & DrawItemState.Selected) != 0;
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(2));
			using (SolidBrush brush = new SolidBrush(Theme.Card))
			{
				graphics.FillRectangle(brush, e.Bounds);
			}
			Theme.FillRound(graphics, r, Theme.S(8), (valuesMatch || row.Checked) ? Theme.Sel : ((e.Index == hover) ? Theme.CardHover : Theme.Card));
			string scanAlreadyText = (row.Already ? Lang.T("scan.already") : (row.RendererLike ? Lang.F("scan.renderer.tag", (int)row.Gpu) : (row.Running ? Lang.T("scan.running.tag") : (row.NeedsChoice ? Lang.T("scan.choice.tag") : (row.Pinned ? Lang.T("scan.picked.tag") : ((!row.Installed) ? Lang.T("scan.stopped.tag") : ""))))));
			string formatMemoryResult = (row.Running ? RunningProgram.FormatMemory(row.Memory) : "");
			if (row.Running && row.Count > 1)
			{
				formatMemoryResult = formatMemoryResult + ((formatMemoryResult.Length > 0) ? " · " : "") + Lang.F("white.pick.procs", row.Count);
			}
			RowLayout rowLayout = LayoutRow(e.Bounds, scanAlreadyText, formatMemoryResult);
			Rectangle check = rowLayout.Check;
			using (GraphicsPath path = Theme.Rounded(check, Theme.S(4)))
			{
				using (SolidBrush brush2 = new SolidBrush(row.Checked ? Theme.Accent : Theme.Bg))
				{
					graphics.FillPath(brush2, path);
				}
				using (Pen pen = new Pen(row.Checked ? Theme.Accent : (row.Already ? Theme.Faint : Theme.StrokeHi)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			if (row.Checked)
			{
				using (Pen pen2 = new Pen(Theme.OnAccent, Math.Max(1.4f, Theme.S(2))))
				{
					graphics.DrawLines(pen2, new Point[3]
					{
						new Point(check.Left + Theme.S(4), check.Top + Theme.S(8)),
						new Point(check.Left + Theme.S(7), check.Top + Theme.S(11)),
						new Point(check.Left + Theme.S(12), check.Top + Theme.S(5))
					});
				}
			}
			lock (iconGate)
			{
				Bitmap value;
				if (iconCache.TryGetValue(row.Path, out value) && value != null)
				{
					graphics.DrawImage(value, rowLayout.Icon);
				}
				else
				{
					Glyphs.Draw(graphics, "game", rowLayout.Icon, row.Already ? Theme.Faint : Theme.Accent);
				}
			}
			TextFormatFlags textFormatFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
			TextRenderer.DrawText(graphics, DisplayRowName(row), Theme.UI(9.2f, true), rowLayout.Name, row.Already ? Theme.Faint : Theme.Fg, textFormatFlags | TextFormatFlags.EndEllipsis);
			TextRenderer.DrawText(graphics, row.Path, Theme.UI(7.6f, false), rowLayout.Path, Theme.Dim, textFormatFlags | TextFormatFlags.PathEllipsis);
			Color foreColor = (row.Already ? Theme.Faint : (row.RendererLike ? Theme.Accent : (row.Running ? Theme.Green : Theme.Dim)));
			if (scanAlreadyText.Length > 0)
			{
				TextRenderer.DrawText(graphics, scanAlreadyText, Theme.UI(7.6f, true), rowLayout.Status, foreColor, textFormatFlags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
			}
			if (formatMemoryResult.Length > 0)
			{
				TextRenderer.DrawText(graphics, formatMemoryResult, Theme.UI(7.6f, false), rowLayout.Details, Theme.Faint, textFormatFlags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
			}
		}

		private void Post(Action a)
		{
			if (closed || !base.IsHandleCreated)
			{
				return;
			}
			try
			{
				BeginInvoke((MethodInvoker)delegate
				{
					if (!closed && !base.IsDisposed)
					{
						a();
					}
				});
			}
			catch
			{
			}
		}

		private void Accept()
		{
			Selected.Clear();
			foreach (Row row in rows)
			{
				if (row.Checked && !row.Already)
				{
					Selected.Add(new ScanHit
					{
						Name = row.Name,
						Root = row.Root,
						Exe = row.Path
					});
				}
			}
			if (Selected.Count != 0)
			{
				base.DialogResult = DialogResult.OK;
			}
		}
	}
}
