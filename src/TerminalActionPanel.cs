using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TerminalActionPanel
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTests.Run();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string sid;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                sid = identity.User == null ? Environment.UserName : identity.User.Value;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\TerminalActionPanel-" + sid, out created))
            {
                if (!created)
                {
                    MessageBox.Show("Terminal Action Panel ya está abierto para este usuario.",
                        "Terminal Action Panel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.Run(new MainForm());
            }

            return 0;
        }
    }

    internal sealed class MainForm : Form
    {
        private const int MaxCommandCharacters = 1000000;
        private const int MaxTranscriptCharacters = 5000000;

        private readonly TranscriptBuffer _transcript = new TranscriptBuffer(MaxTranscriptCharacters);
        private readonly PromptMarkerFilter _markerFilter;
        private readonly AnsiTextFilter _ansiFilter;
        private readonly string _promptMarker;
        private readonly RichTextBox _output;
        private readonly StatusLabel _status;
        private readonly Label _target;
        private readonly Label _identity;
        private readonly Button _pasteButton;
        private readonly Button _copyButton;
        private readonly Button _clearButton;
        private readonly Button _sendButton;
        private readonly Button _interruptButton;
        private readonly TextBox _processInput;
        private ConPtySession _session;
        private bool _ready;
        private bool _busy;
        private bool _closing;

        public MainForm()
        {
            _promptMarker = "__TAP_READY_" + Guid.NewGuid().ToString("N") + "__";
            _markerFilter = new PromptMarkerFilter(_promptMarker);
            _ansiFilter = new AnsiTextFilter();

            Text = "Terminal Action Panel";
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(760, workingArea.Width - 40), Math.Min(520, workingArea.Height - 40));
            Size = new Size(Math.Min(1100, workingArea.Width - 60), Math.Min(740, workingArea.Height - 60));
            BackColor = Color.FromArgb(244, 247, 251);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Font;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(14);
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            Controls.Add(layout);

            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.ColumnCount = 1;
            top.RowCount = 2;
            top.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            top.BackColor = Color.White;
            top.Padding = new Padding(10, 3, 10, 4);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            FlowLayoutPanel targetRow = new FlowLayoutPanel();
            targetRow.Dock = DockStyle.Fill;
            targetRow.WrapContents = false;
            targetRow.FlowDirection = FlowDirection.LeftToRight;
            targetRow.Padding = new Padding(2, 5, 0, 0);
            _target = new Label();
            _target.AutoSize = true;
            _target.Text = "OBJETIVO  PowerShell 5.1 · sesión nueva ConPTY";
            _target.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
            _target.ForeColor = Color.FromArgb(28, 44, 68);
            targetRow.Controls.Add(_target);
            _identity = new Label();
            _identity.AutoSize = true;
            _identity.Margin = new Padding(18, 2, 0, 0);
            _identity.ForeColor = Color.FromArgb(84, 99, 120);
            _identity.Text = GetEffectiveIdentity();
            targetRow.Controls.Add(_identity);
            top.Controls.Add(targetRow, 0, 0);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.Padding = new Padding(0, 2, 0, 0);
            _pasteButton = CreateButton("PASTE + EXECUTE", Color.FromArgb(35, 86, 205), Color.White, 166);
            _pasteButton.Click += PasteAndExecute;
            actions.Controls.Add(_pasteButton);
            _copyButton = CreateButton("COPY ALL", Color.FromArgb(232, 237, 245), Color.FromArgb(29, 46, 69), 120);
            _copyButton.Click += CopyAll;
            actions.Controls.Add(_copyButton);
            _clearButton = CreateButton("CLEAR ALL", Color.FromArgb(255, 235, 232), Color.FromArgb(155, 49, 43), 120);
            _clearButton.Click += ClearAll;
            actions.Controls.Add(_clearButton);
            top.Controls.Add(actions, 0, 1);
            layout.Controls.Add(top, 0, 0);

            Label warning = new Label();
            warning.Dock = DockStyle.Fill;
            warning.TextAlign = ContentAlignment.MiddleLeft;
            warning.Padding = new Padding(8, 0, 4, 0);
            warning.Text = "El contenido del portapapeles se ejecuta inmediatamente en PowerShell con los permisos indicados arriba.";
            warning.ForeColor = Color.FromArgb(143, 72, 25);
            warning.BackColor = Color.FromArgb(255, 246, 228);
            layout.Controls.Add(warning, 0, 1);

            _output = new RichTextBox();
            _output.Dock = DockStyle.Fill;
            _output.ReadOnly = true;
            _output.DetectUrls = false;
            _output.HideSelection = false;
            _output.WordWrap = false;
            _output.MaxLength = Int32.MaxValue;
            _output.BorderStyle = BorderStyle.FixedSingle;
            _output.BackColor = Color.FromArgb(15, 21, 31);
            _output.ForeColor = Color.FromArgb(221, 231, 243);
            _output.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _output.ScrollBars = RichTextBoxScrollBars.Both;
            _output.ContextMenuStrip = CreateOutputMenu();
            layout.Controls.Add(_output, 0, 2);

            TableLayoutPanel inputRow = new TableLayoutPanel();
            inputRow.Dock = DockStyle.Fill;
            inputRow.ColumnCount = 4;
            inputRow.RowCount = 1;
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108F));
            inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
            Label inputHint = new Label();
            inputHint.Dock = DockStyle.Fill;
            inputHint.TextAlign = ContentAlignment.MiddleLeft;
            inputHint.Text = "Entrada al proceso:";
            inputHint.ForeColor = Color.FromArgb(77, 91, 111);
            inputRow.Controls.Add(inputHint, 0, 0);
            _processInput = new TextBox();
            _processInput.Dock = DockStyle.Fill;
            _processInput.Enabled = false;
            _processInput.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
            _processInput.AccessibleName = "Entrada al proceso";
            _processInput.AccessibleDescription = "Entrada para el proceso activo; pulsa Enter para enviarla.";
            _processInput.KeyDown += ProcessInputKeyDown;
            inputRow.Controls.Add(_processInput, 1, 0);
            _sendButton = CreateButton("ENVIAR", Color.FromArgb(232, 237, 245), Color.FromArgb(29, 46, 69), 98);
            _sendButton.Dock = DockStyle.Fill;
            _sendButton.Enabled = false;
            _sendButton.Click += SendProcessInput;
            inputRow.Controls.Add(_sendButton, 2, 0);
            _interruptButton = CreateButton("INTERRUMPIR", Color.FromArgb(255, 235, 232), Color.FromArgb(155, 49, 43), 116);
            _interruptButton.Dock = DockStyle.Fill;
            _interruptButton.Enabled = false;
            _interruptButton.Click += InterruptProcess;
            inputRow.Controls.Add(_interruptButton, 3, 0);
            layout.Controls.Add(inputRow, 0, 3);

            _status = new StatusLabel();
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.ForeColor = Color.FromArgb(77, 91, 111);
            _status.Text = "Iniciando la sesión de PowerShell…";
            _status.AccessibleName = _status.Text;
            _status.NotifyNameChanged();
            layout.Controls.Add(_status, 0, 4);

            Shown += StartTerminal;
            Resize += ResizeTerminal;
            FormClosing += ConfirmClose;
        }

        private static Button CreateButton(string text, Color backColor, Color foreColor, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 34;
            button.Margin = new Padding(0, 0, 9, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
            button.Cursor = Cursors.Hand;
            button.TabStop = true;
            button.Tag = new Color[] { backColor, foreColor };
            button.EnabledChanged += delegate(object sender, EventArgs eventArgs)
            {
                Button changed = (Button)sender;
                Color[] colors = (Color[])changed.Tag;
                changed.BackColor = changed.Enabled ? colors[0] : Color.FromArgb(235, 238, 242);
                changed.ForeColor = changed.Enabled ? colors[1] : Color.FromArgb(148, 156, 168);
            };
            return button;
        }

        private static string GetEffectiveIdentity()
        {
            string account;
            bool elevated;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                account = identity.Name;
                elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }

            return "Usuario: " + account + " · token efectivo: " + (elevated ? "administrador" : "estándar / no elevado");
        }

        private ContextMenuStrip CreateOutputMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem copy = new ToolStripMenuItem("Copiar todo");
            copy.Click += CopyAll;
            menu.Items.Add(copy);
            ToolStripMenuItem clear = new ToolStripMenuItem("Limpiar todo");
            clear.Click += ClearAll;
            menu.Items.Add(clear);
            return menu;
        }

        private void StartTerminal(object sender, EventArgs e)
        {
            try
            {
                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string powerShell = Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                string initScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host-init.ps1");
                if (!File.Exists(powerShell))
                {
                    throw new FileNotFoundException("No se encontró Windows PowerShell 5.1.", powerShell);
                }
                if (!File.Exists(initScript))
                {
                    throw new FileNotFoundException("Falta host-init.ps1 junto a la aplicación.", initScript);
                }

                string arguments = "-NoLogo -NoExit -NoProfile -ExecutionPolicy Bypass -File " +
                    ConPtySession.QuoteArgument(initScript) + " " + ConPtySession.QuoteArgument(_promptMarker);
                _session = new ConPtySession(120, 32);
                _session.OutputReceived += SessionOutputReceived;
                _session.Exited += SessionExited;
                _session.Start(powerShell, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                ResizeTerminal(this, EventArgs.Empty);
                SetStatus("Abriendo PowerShell mediante ConPTY…", false);
            }
            catch (Exception exception)
            {
                SetStatus("No se pudo iniciar la terminal: " + exception.Message, true);
                _pasteButton.Enabled = false;
            }
        }

        private void SessionOutputReceived(string text)
        {
            QueueOnUi(delegate
            {
                int markerCount;
                string visibleText = _markerFilter.Feed(_ansiFilter.Feed(text), out markerCount);
                if (visibleText.Length > 0)
                {
                    AppendOutput(visibleText);
                }
                if (markerCount > 0)
                {
                    _ready = true;
                    _busy = false;
                    SetStatus("Lista · PowerShell 5.1 · sesión ConPTY", false);
                    UpdateControls();
                }
            });
        }

        private void SessionExited()
        {
            QueueOnUi(delegate
            {
                string remaining = _markerFilter.Flush();
                if (remaining.Length > 0)
                {
                    AppendOutput(remaining);
                }
                _ready = false;
                _busy = false;
                SetStatus("La terminal se cerró.", true);
                UpdateControls();
            });
        }

        private void QueueOnUi(MethodInvoker action)
        {
            if (_closing || IsDisposed || !IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // The form may be closing while the ConPTY reader drains its final output.
            }
            catch (InvalidOperationException)
            {
                // The form may be closing while the ConPTY reader drains its final output.
            }
        }

        private void AppendOutput(string text)
        {
            bool wasAtBottom = _output.SelectionStart + _output.SelectionLength >= _output.TextLength - 2;
            int oldLength = _transcript.Length;
            _transcript.Append(text);
            int dropped = oldLength + text.Length - _transcript.Length;
            if (dropped > 0 && _output.TextLength > 0)
            {
                _output.Select(0, Math.Min(dropped, _output.TextLength));
                _output.SelectedText = String.Empty;
            }
            _output.AppendText(text);
            if (wasAtBottom)
            {
                _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
        }

        private void PasteAndExecute(object sender, EventArgs e)
        {
            if (!_ready || _busy || _session == null || _session.HasExited)
            {
                SetStatus("La sesión no está lista para recibir otro comando.", true);
                return;
            }

            string clipboardText;
            try
            {
                clipboardText = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : String.Empty;
            }
            catch (Exception exception)
            {
                SetStatus("No se pudo leer el portapapeles: " + exception.Message, true);
                return;
            }

            if (String.IsNullOrWhiteSpace(clipboardText))
            {
                SetStatus("El portapapeles está vacío; no se ejecutó nada.", true);
                return;
            }
            if (clipboardText.Length > MaxCommandCharacters)
            {
                SetStatus("El texto supera el límite de 1 000 000 caracteres.", true);
                return;
            }

            try
            {
                string input = PowerShellInput.WrapInCurrentScope(clipboardText);
                _busy = true;
                _ready = false;
                SetStatus("Ejecutando el contenido del portapapeles…", false);
                UpdateControls();
                _session.WriteInput(input);
            }
            catch (Exception exception)
            {
                _busy = false;
                SetStatus("No se pudo enviar el comando: " + exception.Message, true);
                UpdateControls();
            }
        }

        private void CopyAll(object sender, EventArgs e)
        {
            string text = _transcript.ToString();
            if (text.Length == 0)
            {
                SetStatus("No hay salida disponible para copiar.", true);
                return;
            }

            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                SetStatus("Copiados " + text.Length.ToString("N0") + " caracteres del historial disponible.", false);
            }
            catch (Exception exception)
            {
                SetStatus("No se pudo escribir en el portapapeles: " + exception.Message, true);
            }
        }

        private void ClearAll(object sender, EventArgs e)
        {
            _transcript.Clear();
            _output.Clear();
            SetStatus("Transcripción y scrollback de la aplicación eliminados.", false);
        }

        private void ProcessInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                SendProcessInput(sender, EventArgs.Empty);
            }
        }

        private void SendProcessInput(object sender, EventArgs e)
        {
            if (!_busy || _session == null || _session.HasExited)
            {
                return;
            }
            try
            {
                _session.WriteInput(_processInput.Text + "\r");
                _processInput.Clear();
                _processInput.Focus();
            }
            catch (Exception exception)
            {
                SetStatus("No se pudo enviar la entrada: " + exception.Message, true);
            }
        }

        private void InterruptProcess(object sender, EventArgs e)
        {
            if (!_busy || _session == null || _session.HasExited)
            {
                return;
            }
            try
            {
                _session.SendCtrlC();
                SetStatus("Se envió Ctrl+C al proceso activo…", false);
            }
            catch (Exception exception)
            {
                SetStatus("No se pudo interrumpir el proceso: " + exception.Message, true);
            }
        }

        private void ResizeTerminal(object sender, EventArgs e)
        {
            if (_session == null || _output.ClientSize.Width < 20 || _output.ClientSize.Height < 20)
            {
                return;
            }
            int characterWidth = Math.Max(1, TextRenderer.MeasureText("W", _output.Font).Width);
            int lineHeight = Math.Max(1, TextRenderer.MeasureText("W", _output.Font).Height);
            int columns = Math.Max(20, (_output.ClientSize.Width - 24) / characterWidth);
            int rows = Math.Max(5, _output.ClientSize.Height / lineHeight);
            try
            {
                _session.Resize(columns, rows);
            }
            catch
            {
                // A resize racing with shutdown has no user-visible consequence.
            }
        }

        private void UpdateControls()
        {
            bool active = _busy && _session != null && !_session.HasExited;
            _pasteButton.Enabled = _ready && !_busy && _session != null && !_session.HasExited;
            _processInput.Enabled = active;
            _sendButton.Enabled = active;
            _interruptButton.Enabled = active;
        }

        private void SetStatus(string text, bool isError)
        {
            _status.Text = text;
            _status.AccessibleName = text;
            _status.ForeColor = isError ? Color.FromArgb(166, 47, 40) : Color.FromArgb(77, 91, 111);
            _status.NotifyNameChanged();
        }

        private void ConfirmClose(object sender, FormClosingEventArgs e)
        {
            if (_busy && !_closing)
            {
                DialogResult answer = MessageBox.Show(
                    "Hay un comando en ejecución. Cerrar la ventana terminará la sesión de PowerShell y los procesos que haya iniciado. ¿Quieres continuar?",
                    "Cerrar la sesión", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _closing = true;
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
        }
    }

    internal sealed class StatusLabel : Label
    {
        public void NotifyNameChanged()
        {
            if (IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            }
        }
    }

    internal static class PowerShellInput
    {
        public static string WrapInCurrentScope(string text)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split(new char[] { '\n' });
            StringBuilder input = new StringBuilder(normalized.Length + lines.Length + 8);
            input.Append(". {\r");
            for (int index = 0; index < lines.Length; index++)
            {
                input.Append(lines[index]);
                input.Append('\r');
            }
            input.Append("}\r");
            return input.ToString();
        }
    }

    internal sealed class TranscriptBuffer
    {
        private readonly int _capacity;
        private readonly StringBuilder _content = new StringBuilder();

        public TranscriptBuffer(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException("capacity");
            }
            _capacity = capacity;
        }

        public int Length
        {
            get { return _content.Length; }
        }

        public void Append(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }
            _content.Append(text);
            int excess = _content.Length - _capacity;
            if (excess > 0)
            {
                _content.Remove(0, excess);
            }
        }

        public void Clear()
        {
            _content.Length = 0;
        }

        public override string ToString()
        {
            return _content.ToString();
        }
    }

    internal sealed class AnsiTextFilter
    {
        private enum EscapeState
        {
            Plain,
            Escape,
            Csi,
            Osc,
            OscEscape
        }

        private EscapeState _state;
        private bool _lastWasCarriageReturn;

        public string Feed(string text)
        {
            StringBuilder output = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (_state == EscapeState.Escape)
                {
                    if (value == '[')
                    {
                        _state = EscapeState.Csi;
                    }
                    else if (value == ']')
                    {
                        _state = EscapeState.Osc;
                    }
                    else
                    {
                        _state = EscapeState.Plain;
                    }
                    continue;
                }
                if (_state == EscapeState.Csi)
                {
                    if (value >= '@' && value <= '~')
                    {
                        _state = EscapeState.Plain;
                    }
                    continue;
                }
                if (_state == EscapeState.Osc)
                {
                    if (value == '\a')
                    {
                        _state = EscapeState.Plain;
                    }
                    else if (value == '\u001b')
                    {
                        _state = EscapeState.OscEscape;
                    }
                    continue;
                }
                if (_state == EscapeState.OscEscape)
                {
                    _state = value == '\\' ? EscapeState.Plain : EscapeState.Osc;
                    continue;
                }
                if (value == '\u001b')
                {
                    _state = EscapeState.Escape;
                    continue;
                }
                if (value == '\r')
                {
                    output.Append('\n');
                    _lastWasCarriageReturn = true;
                    continue;
                }
                if (value == '\n')
                {
                    if (!_lastWasCarriageReturn)
                    {
                        output.Append('\n');
                    }
                    _lastWasCarriageReturn = false;
                    continue;
                }
                _lastWasCarriageReturn = false;
                if (value == '\t' || value >= ' ')
                {
                    output.Append(value);
                }
            }
            return output.ToString();
        }

        public void Reset()
        {
            _state = EscapeState.Plain;
            _lastWasCarriageReturn = false;
        }
    }

    internal sealed class PromptMarkerFilter
    {
        private readonly string _marker;
        private readonly StringBuilder _pending = new StringBuilder();

        public PromptMarkerFilter(string marker)
        {
            _marker = marker;
        }

        public string Feed(string text, out int markerCount)
        {
            markerCount = 0;
            _pending.Append(text);
            StringBuilder visible = new StringBuilder();
            int markerIndex;
            while ((markerIndex = _pending.ToString().IndexOf(_marker, StringComparison.Ordinal)) >= 0)
            {
                visible.Append(_pending.ToString(0, markerIndex));
                _pending.Remove(0, markerIndex + _marker.Length);
                markerCount++;
            }

            string remaining = _pending.ToString();
            int keep = Math.Min(_marker.Length - 1, remaining.Length);
            while (keep > 0 && String.Compare(remaining, remaining.Length - keep, _marker, 0, keep, StringComparison.Ordinal) != 0)
            {
                keep--;
            }
            int flushLength = Math.Max(0, _pending.Length - keep);
            if (flushLength > 0)
            {
                visible.Append(_pending.ToString(0, flushLength));
                _pending.Remove(0, flushLength);
            }
            return visible.ToString();
        }

        public string Flush()
        {
            string value = _pending.ToString();
            _pending.Length = 0;
            return value;
        }

        public void Reset()
        {
            _pending.Length = 0;
        }
    }

    internal sealed class ConPtySession : IDisposable
    {
        private const uint ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateSuspended = 0x00000004;
        private const uint HandleFlagInherit = 0x00000001;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint Infinite = 0xFFFFFFFF;

        private readonly object _writeLock = new object();
        private readonly int _initialColumns;
        private readonly int _initialRows;
        private IntPtr _pseudoConsole = IntPtr.Zero;
        private IntPtr _inputWrite = IntPtr.Zero;
        private IntPtr _outputRead = IntPtr.Zero;
        private IntPtr _process = IntPtr.Zero;
        private IntPtr _thread = IntPtr.Zero;
        private IntPtr _job = IntPtr.Zero;
        private IntPtr _attributeList = IntPtr.Zero;
        private Thread _reader;
        private Thread _watcher;
        private int _disposed;
        private int _closing;
        private volatile bool _hasExited;
        private volatile uint _exitCode;

        public event Action<string> OutputReceived;
        public event Action Exited;

        public ConPtySession(int columns, int rows)
        {
            _initialColumns = Math.Max(20, columns);
            _initialRows = Math.Max(5, rows);
        }

        public bool HasExited
        {
            get { return _hasExited; }
        }

        public uint ExitCode
        {
            get { return _exitCode; }
        }

        public void Start(string executable, string arguments, string workingDirectory)
        {
            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            bool pseudoConsoleCreated = false;
            try
            {
                SecurityAttributes security = new SecurityAttributes();
                security.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                security.InheritHandle = true;
                if (!CreatePipe(out inputRead, out inputWrite, ref security, 0))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear la entrada de ConPTY.");
                }
                if (!CreatePipe(out outputRead, out outputWrite, ref security, 0))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear la salida de ConPTY.");
                }
                if (!SetHandleInformation(inputWrite, HandleFlagInherit, 0) || !SetHandleInformation(outputRead, HandleFlagInherit, 0))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudieron proteger los extremos de los canales de ConPTY.");
                }

                Coord size = new Coord((short)_initialColumns, (short)_initialRows);
                int result = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }
                pseudoConsoleCreated = true;
                _inputWrite = inputWrite;
                inputWrite = IntPtr.Zero;
                _outputRead = outputRead;
                outputRead = IntPtr.Zero;

                IntPtr attributeListBytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListBytes);
                if (attributeListBytes == IntPtr.Zero)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo preparar el inicio de ConPTY.");
                }
                _attributeList = Marshal.AllocHGlobal(attributeListBytes);
                if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attributeListBytes))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo inicializar el inicio de ConPTY.");
                }
                if (!UpdateProcThreadAttribute(_attributeList, 0, new IntPtr(ProcThreadAttributePseudoConsole),
                    _pseudoConsole, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo asociar la sesión ConPTY al proceso.");
                }

                StartupInfoEx startup = new StartupInfoEx();
                startup.StartupInfo.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                startup.AttributeList = _attributeList;
                ProcessInformation processInfo;
                string commandLine = QuoteArgument(executable) + (String.IsNullOrEmpty(arguments) ? String.Empty : " " + arguments);
                bool started = CreateProcess(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                    false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    IntPtr.Zero, workingDirectory, ref startup, out processInfo);
                if (!started)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo iniciar PowerShell dentro de ConPTY.");
                }
                // Keep the pipe ends passed to CreatePseudoConsole alive until the client process attaches.
                CloseHandle(inputRead);
                inputRead = IntPtr.Zero;
                CloseHandle(outputWrite);
                outputWrite = IntPtr.Zero;
                _process = processInfo.Process;
                _thread = processInfo.Thread;

                TryCreateKillOnCloseJob(_process);
                if (ResumeThread(_thread) == UInt32.MaxValue)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo iniciar el proceso de PowerShell.");
                }

                _reader = new Thread(ReadOutput);
                _reader.IsBackground = true;
                _reader.Name = "Terminal Action Panel ConPTY reader";
                _reader.Start();

                _watcher = new Thread(WatchProcess);
                _watcher.IsBackground = true;
                _watcher.Name = "Terminal Action Panel process watcher";
                _watcher.Start();
            }
            catch
            {
                if (_process != IntPtr.Zero)
                {
                    TerminateProcess(_process, 0);
                }
                if (_job != IntPtr.Zero)
                {
                    CloseHandle(_job);
                    _job = IntPtr.Zero;
                }
                if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
                if (inputWrite != IntPtr.Zero) CloseHandle(inputWrite);
                if (outputRead != IntPtr.Zero) CloseHandle(outputRead);
                if (outputWrite != IntPtr.Zero) CloseHandle(outputWrite);
                if (pseudoConsoleCreated && _pseudoConsole != IntPtr.Zero) ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
                DisposeHandles();
                throw;
            }
            finally
            {
                if (_attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(_attributeList);
                    Marshal.FreeHGlobal(_attributeList);
                    _attributeList = IntPtr.Zero;
                }
            }
        }

        private void TryCreateKillOnCloseJob(IntPtr process)
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return;
            }
            ExtendedLimitInformation limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExtendedLimitInformation)));
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer,
                    (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))) || !AssignProcessToJobObject(job, process))
                {
                    CloseHandle(job);
                    return;
                }
                _job = job;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void ReadOutput()
        {
            byte[] bytes = new byte[8192];
            char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            Decoder decoder = new UTF8Encoding(false, false).GetDecoder();
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    uint count;
                    bool success = ReadFile(_outputRead, bytes, (uint)bytes.Length, out count, IntPtr.Zero);
                    if (!success || count == 0)
                    {
                        break;
                    }
                    int chars = decoder.GetChars(bytes, 0, (int)count, characters, 0, false);
                    if (chars > 0)
                    {
                        Action<string> handler = OutputReceived;
                        if (handler != null)
                        {
                            handler(new String(characters, 0, chars));
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutdown closes the output pipe to unblock the reader.
            }
        }

        private void WatchProcess()
        {
            if (_process != IntPtr.Zero)
            {
                WaitForSingleObject(_process, Infinite);
                uint exitCode;
                if (GetExitCodeProcess(_process, out exitCode))
                {
                    _exitCode = exitCode;
                }
            }
            _hasExited = true;
            if (Volatile.Read(ref _disposed) == 0)
            {
                Action handler = Exited;
                if (handler != null)
                {
                    handler();
                }
            }
        }

        public void WriteInput(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                return;
            }
            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _disposed) != 0 || _hasExited)
            {
                throw new InvalidOperationException("La sesión de PowerShell está cerrada.");
            }

            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            lock (_writeLock)
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    uint written;
                    int remaining = bytes.Length - offset;
                    byte[] segment;
                    if (offset == 0 && remaining == bytes.Length)
                    {
                        segment = bytes;
                    }
                    else
                    {
                        segment = new byte[remaining];
                        Buffer.BlockCopy(bytes, offset, segment, 0, remaining);
                    }
                    if (!WriteFile(_inputWrite, segment, (uint)segment.Length, out written, IntPtr.Zero) || written == 0)
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo escribir en la sesión ConPTY.");
                    }
                    offset += (int)written;
                }
            }
        }

        public void SendCtrlC()
        {
            if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _disposed) != 0)
            {
                throw new InvalidOperationException("La sesión de PowerShell está cerrándose.");
            }
            byte[] interrupt = new byte[] { 3 };
            uint written;
            lock (_writeLock)
            {
                if (!WriteFile(_inputWrite, interrupt, (uint)interrupt.Length, out written, IntPtr.Zero) || written != 1)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo enviar Ctrl+C a ConPTY.");
                }
            }
        }

        public void Resize(int columns, int rows)
        {
            if (_pseudoConsole == IntPtr.Zero || Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            Coord size = new Coord((short)Math.Max(1, Math.Min(Int16.MaxValue, columns)),
                (short)Math.Max(1, Math.Min(Int16.MaxValue, rows)));
            int result = ResizePseudoConsole(_pseudoConsole, size);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }

        public static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int slashes = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0)
                {
                    result.Append('\\', slashes);
                    slashes = 0;
                }
                result.Append(character);
            }
            if (slashes > 0)
            {
                result.Append('\\', slashes * 2);
            }
            result.Append('"');
            return result.ToString();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
            {
                return;
            }

            if (_process != IntPtr.Zero)
            {
                TerminateProcess(_process, 0);
            }
            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }
            if (_reader != null && _reader != Thread.CurrentThread)
            {
                _reader.Join(2000);
            }
            if (_watcher != null && _watcher != Thread.CurrentThread)
            {
                _watcher.Join(2000);
            }
            if (_job != IntPtr.Zero)
            {
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }
            Interlocked.Exchange(ref _disposed, 1);
            DisposeHandles();
        }

        private void DisposeHandles()
        {
            if (_inputWrite != IntPtr.Zero) { CloseHandle(_inputWrite); _inputWrite = IntPtr.Zero; }
            if (_outputRead != IntPtr.Zero) { CloseHandle(_outputRead); _outputRead = IntPtr.Zero; }
            if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
            if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;
            public Coord(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public JobBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute,
            IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal static class SelfTests
    {
        private static string _logPath;

        public static int Run()
        {
            _logPath = Path.Combine(Path.GetTempPath(), "TerminalActionPanel-self-test-" + Process.GetCurrentProcess().Id + ".log");
            File.WriteAllText(_logPath, String.Empty);

            try
            {
                TestAnsiTextFilter();
                TestMarkerFilter();
                TestTranscriptBuffer();
                TestPowerShellInput();
                TestConPtyCommandPrompt();
                TestConPtyPowerShell();
                Report("PASS: todas las pruebas de Terminal Action Panel finalizaron correctamente.");
                return 0;
            }
            catch (Exception exception)
            {
                Report("FAIL: " + exception.ToString());
                return 1;
            }
        }

        private static void Report(string message)
        {
            File.AppendAllText(_logPath, message + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine(message);
        }

        private static void TestAnsiTextFilter()
        {
            AnsiTextFilter filter = new AnsiTextFilter();
            string result = filter.Feed("a\u001b[31mrojo\u001b[0m\r\nb\u001b]0;title\u0007");
            Assert(result == "arojo\nb", "El filtro debe quitar secuencias VT/OSC y normalizar saltos de línea.");
            Report("PASS: filtrado de secuencias VT/OSC.");
        }

        private static void TestMarkerFilter()
        {
            PromptMarkerFilter filter = new PromptMarkerFilter("__MARKER__");
            int count;
            string first = filter.Feed("antes__MAR", out count);
            Assert(first == "antes" && count == 0, "El detector debe retener prefijos parciales del marcador.");
            string second = filter.Feed("KER__después", out count);
            Assert(second == "después" && count == 1, "El detector debe retirar un marcador dividido entre lecturas.");
            Report("PASS: detección de prompts divididos entre lecturas.");
        }

        private static void TestTranscriptBuffer()
        {
            TranscriptBuffer buffer = new TranscriptBuffer(5);
            buffer.Append("abc");
            buffer.Append("defg");
            Assert(buffer.ToString() == "cdefg", "La transcripción debe conservar exactamente el tramo más reciente dentro del límite.");
            buffer.Clear();
            Assert(buffer.Length == 0 && buffer.ToString() == String.Empty, "CLEAR ALL debe vaciar el historial temporal.");
            Report("PASS: límite y limpieza de la transcripción.");
        }

        private static void TestPowerShellInput()
        {
            string wrapped = PowerShellInput.WrapInCurrentScope("$x = 1\r\nWrite-Output $x\n");
            Assert(wrapped == ". {\r$x = 1\rWrite-Output $x\r\r}\r", "El envoltorio debe conservar líneas múltiples y normalizar Enter.");
            Report("PASS: envoltorio de comandos multilínea.");
        }

        private static void TestConPtyPowerShell()
        {
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string executable = Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string initScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host-init.ps1");
            Assert(File.Exists(executable), "No se encontró Windows PowerShell 5.1 para la prueba de integración.");
            Assert(File.Exists(initScript), "No se encontró host-init.ps1 para la prueba de integración.");

            string marker = "__TAP_TEST_" + Guid.NewGuid().ToString("N") + "__";
            string arguments = "-NoLogo -NoExit -NoProfile -ExecutionPolicy Bypass -File " +
                ConPtySession.QuoteArgument(initScript) + " " + ConPtySession.QuoteArgument(marker);
            StringBuilder received = new StringBuilder();
            object sync = new object();
            using (ConPtySession session = new ConPtySession(100, 25))
            {
                session.OutputReceived += delegate(string text)
                {
                    lock (sync) { received.Append(text); }
                };
                session.Start(executable, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                bool promptStarted = WaitForText(received, sync, marker, 20000);
                if (!promptStarted)
                {
                    string startupOutput;
                    lock (sync) { startupOutput = received.ToString(); }
                    throw new InvalidOperationException("ConPTY no devolvió el marcador inicial de PowerShell. Proceso cerrado: " + session.HasExited + "; código: " + session.ExitCode + ". Salida recibida: " + startupOutput);
                }
                Assert(CountOccurrences(received, sync, marker) == 1, "El prompt inicial debe emitir un único marcador de disponibilidad.");
                session.WriteInput(PowerShellInput.WrapInCurrentScope("Write-Output 'TAP_MULTILINE_OK'\nWrite-Output 'TAP_SECOND_LINE'\nWrite-Output 'TAP_COMMAND_DONE'"));
                Assert(WaitForText(received, sync, "TAP_MULTILINE_OK", 20000), "PowerShell no ejecutó el comando multilínea enviado por ConPTY.");
                Assert(WaitForText(received, sync, "TAP_SECOND_LINE", 5000), "PowerShell no ejecutó la segunda línea del comando multilínea.");
                Assert(WaitForText(received, sync, "TAP_COMMAND_DONE", 5000), "PowerShell no ejecutó la última línea del comando multilínea.");
                Assert(WaitForMarkerCount(received, sync, marker, 2, 10000), "No se observó el prompt de PowerShell al completar el comando.");
                Assert(CountOccurrences(received, sync, marker) == 2, "Un comando multilínea no debe anunciar disponibilidad antes de completar el bloque.");

                string hereString = "$here = @\"\nTAP_HERE_LINE1\nTAP_HERE_LINE2\n\"@\nWrite-Output $here";
                session.WriteInput(PowerShellInput.WrapInCurrentScope(hereString));
                Assert(WaitForText(received, sync, "TAP_HERE_LINE1", 10000), "PowerShell no ejecutó la primera línea de un here-string.");
                Assert(WaitForText(received, sync, "TAP_HERE_LINE2", 5000), "PowerShell no ejecutó la segunda línea de un here-string.");
                Assert(WaitForMarkerCount(received, sync, marker, 3, 10000), "No se observó el prompt tras el here-string.");
                Assert(CountOccurrences(received, sync, marker) == 3, "El here-string no debe anunciar disponibilidad antes de completarse.");
            }
            Report("PASS: integración real PowerShell 5.1 + ConPTY, comandos multilínea y here-strings.");
        }

        private static void TestConPtyCommandPrompt()
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            StringBuilder received = new StringBuilder();
            object sync = new object();
            using (ConPtySession session = new ConPtySession(80, 25))
            {
                session.OutputReceived += delegate(string text)
                {
                    lock (sync) { received.Append(text); }
                };
                session.Start(executable, "/Q", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                session.WriteInput("echo TAP_CMD_OK\r");
                if (!WaitForText(received, sync, "TAP_CMD_OK", 10000))
                {
                    string startupOutput;
                    lock (sync) { startupOutput = received.ToString(); }
                    throw new InvalidOperationException("ConPTY no pudo ejecutar un comando en cmd.exe. Proceso cerrado: " + session.HasExited + "; código: " + session.ExitCode + ". Salida recibida: " + startupOutput);
                }
            }
            Report("PASS: transmisión de entrada y salida de un proceso real mediante ConPTY.");
        }

        private static bool WaitForText(StringBuilder text, object sync, string value, int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                lock (sync)
                {
                    if (text.ToString().IndexOf(value, StringComparison.Ordinal) >= 0) return true;
                }
                Thread.Sleep(25);
            }
            return false;
        }

        private static bool WaitForMarkerCount(StringBuilder text, object sync, string marker, int minimum, int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                lock (sync)
                {
                    string current = text.ToString();
                    int count = 0;
                    int index = 0;
                    while ((index = current.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
                    {
                        count++;
                        index += marker.Length;
                    }
                    if (count >= minimum) return true;
                }
                Thread.Sleep(25);
            }
            return false;
        }

        private static int CountOccurrences(StringBuilder text, object sync, string value)
        {
            lock (sync)
            {
                string current = text.ToString();
                int count = 0;
                int index = 0;
                while ((index = current.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    index += value.Length;
                }
                return count;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
