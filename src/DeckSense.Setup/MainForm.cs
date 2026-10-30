using System.Diagnostics;
using System.Security.Principal;

namespace DeckSense.Setup;

/// <summary>
/// Das Einrichtungsfenster. Zeigt an, wie es um Dienst und Autostart steht, und
/// lässt beides schalten.
///
/// Der Dienst selbst bleibt fensterlos - das hier ist ein getrenntes Programm,
/// das man zum Einrichten öffnet und danach wieder schließt.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Grund = Color.FromArgb(0x1b, 0x1b, 0x1e);
    private static readonly Color Flaeche = Color.FromArgb(0x23, 0x23, 0x27);
    private static readonly Color Schrift = Color.FromArgb(0xe6, 0xe6, 0xe9);
    private static readonly Color Gedaempft = Color.FromArgb(0x9a, 0x9a, 0xa3);
    private static readonly Color Violett = Color.FromArgb(0x78, 0x00, 0xff);
    private static readonly Color Gut = Color.FromArgb(0x6f, 0xd1, 0x7a);
    private static readonly Color Schlecht = Color.FromArgb(0xff, 0xb0, 0x1f);

    private readonly Label _dienstStatus = Beschriftung();
    private readonly Label _apiStatus = Beschriftung();
    private readonly Label _autostartStatus = Beschriftung();
    private readonly Label _exePfad = Beschriftung();
    private readonly ListView _sensoren = new();
    private readonly Button _dienstKnopf = Knopf("Start service");
    private readonly Button _autostartKnopf = Knopf("Set up autostart");
    private readonly Button _xmlKnopf = Knopf("Show task as XML");
    private readonly Button _installKnopf = Knopf("Install program");
    private readonly Button _cpuTempKnopf = Knopf("CPU temperature...");
    private readonly TextBox _zielOrdner = new();
    private readonly Label _meldung = Beschriftung();
    private readonly System.Windows.Forms.Timer _takt = new() { Interval = 1000 };

    private string? _servicePfad;
    private bool _aktualisierungLaeuft;

    /// <summary>
    /// Der Autostart-Status kommt von <c>schtasks.exe</c>, also aus einem
    /// eigenen Prozess. Den jede Sekunde zu starten wäre absurd für eine
    /// Angabe, die sich nur ändert, wenn man selbst den Knopf drückt. Deshalb
    /// gepuffert: alle fünf Takte, und sofort nach einer Änderung.
    /// </summary>
    private const int AutostartAlleTakte = 5;

    private bool _autostartEingerichtet;
    private int _taktZaehler = int.MaxValue;

    public MainForm()
    {
        Text = "DeckSense Setup";
        BackColor = Grund;
        ForeColor = Schrift;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(620, 560);
        MinimumSize = new Size(560, 460);
        StartPosition = FormStartPosition.CenterScreen;

        _servicePfad = ServiceProbe.ExeFinden();

        Controls.Add(Aufbauen());

        _takt.Tick += async (_, _) => await AktualisierenAsync().ConfigureAwait(true);
        _takt.Start();

        _dienstKnopf.Click += DienstSchalten;
        _autostartKnopf.Click += AutostartSchalten;
        _xmlKnopf.Click += XmlZeigen;
        _installKnopf.Click += Installieren;
        _cpuTempKnopf.Click += CpuTempSchalten;

        _ = AktualisierenAsync();
    }

    private static Label Beschriftung() => new()
    {
        AutoSize = true,
        Margin = new Padding(0, 3, 0, 3),
        ForeColor = Schrift,
    };

    private static Button Knopf(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Flaeche,
        ForeColor = Schrift,
        Padding = new Padding(10, 5, 10, 5),
        Margin = new Padding(0, 0, 8, 0),
    };

    private Control Aufbauen()
    {
        var wurzel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = Grund,
        };
        wurzel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wurzel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        wurzel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wurzel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        wurzel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var status = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12),
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void ZeileAnfuegen(string titel, Control wert)
        {
            status.Controls.Add(new Label { Text = titel, AutoSize = true, ForeColor = Gedaempft, Margin = new Padding(0, 3, 10, 3) });
            status.Controls.Add(wert);
        }

        ZeileAnfuegen("Service", _dienstStatus);
        ZeileAnfuegen("HTTP API", _apiStatus);
        ZeileAnfuegen("Autostart", _autostartStatus);
        ZeileAnfuegen("Program", _exePfad);

        _sensoren.View = View.Details;
        _sensoren.FullRowSelect = true;
        _sensoren.GridLines = false;
        _sensoren.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _sensoren.Dock = DockStyle.Fill;
        _sensoren.BackColor = Flaeche;
        _sensoren.ForeColor = Schrift;
        _sensoren.BorderStyle = BorderStyle.None;
        _sensoren.Columns.Add("Sensor ID", 230);
        _sensoren.Columns.Add("Name", 190);
        _sensoren.Columns.Add("Value", 110, HorizontalAlignment.Right);

        _zielOrdner.Text = Installer.Vorgabeziel;
        _zielOrdner.BackColor = Flaeche;
        _zielOrdner.ForeColor = Schrift;
        _zielOrdner.BorderStyle = BorderStyle.FixedSingle;
        _zielOrdner.Width = 330;
        _zielOrdner.Margin = new Padding(0, 2, 8, 0);

        var installZeile = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 12, 0, 0),
        };
        installZeile.Controls.Add(new Label
        {
            Text = "Target folder",
            AutoSize = true,
            ForeColor = Gedaempft,
            Margin = new Padding(0, 6, 10, 0),
            Width = 100,
        });
        installZeile.Controls.Add(_zielOrdner);
        installZeile.Controls.Add(_installKnopf);

        var knoepfe = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 8, 0, 6),
        };
        knoepfe.Controls.Add(_dienstKnopf);
        knoepfe.Controls.Add(_autostartKnopf);
        knoepfe.Controls.Add(_xmlKnopf);
        knoepfe.Controls.Add(_cpuTempKnopf);

        _meldung.ForeColor = Gedaempft;
        _meldung.MaximumSize = new Size(580, 0);

        wurzel.Controls.Add(status, 0, 0);
        wurzel.Controls.Add(_sensoren, 0, 1);
        wurzel.Controls.Add(installZeile, 0, 2);
        wurzel.Controls.Add(knoepfe, 0, 3);
        wurzel.Controls.Add(_meldung, 0, 4);

        return wurzel;
    }

    private async Task AktualisierenAsync()
    {
        if (_aktualisierungLaeuft)
        {
            return;
        }

        _aktualisierungLaeuft = true;

        try
        {
            DienstStand stand = await ServiceProbe.AbfragenAsync().ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            _dienstStatus.Text = stand.ProzessLaeuft ? "running" : "not running";
            _dienstStatus.ForeColor = stand.ProzessLaeuft ? Gut : Schlecht;

            _apiStatus.Text = stand.ApiErreichbar
                ? $"reachable on 127.0.0.1:5891, version {stand.Version ?? "?"}"
                : "not responding";
            _apiStatus.ForeColor = stand.ApiErreichbar ? Gut : Schlecht;

            if (_taktZaehler >= AutostartAlleTakte)
            {
                _taktZaehler = 0;
                _autostartEingerichtet = TaskSetup.IstEingerichtet();
            }
            else
            {
                _taktZaehler++;
            }

            bool eingerichtet = _autostartEingerichtet;
            _autostartStatus.Text = eingerichtet
                ? $"set up as task \"{TaskSetup.AufgabenName}\""
                : "not set up";
            _autostartStatus.ForeColor = eingerichtet ? Gut : Gedaempft;

            _exePfad.Text = _servicePfad ?? "not found";
            _exePfad.ForeColor = _servicePfad is null ? Schlecht : Gedaempft;

            _dienstKnopf.Text = stand.ProzessLaeuft ? "Stop service" : "Start service";
            _dienstKnopf.Enabled = _servicePfad is not null;
            _autostartKnopf.Text = eingerichtet ? "Remove autostart" : "Set up autostart";
            _autostartKnopf.Enabled = _servicePfad is not null;
            _xmlKnopf.Enabled = _servicePfad is not null;

            bool? cpuTemp = CpuTempSwitch.Lesen(CpuTempSwitch.Pfad(_servicePfad));
            _cpuTempKnopf.Text = cpuTemp switch
            {
                true => "Turn CPU temperature off",
                false => "Turn CPU temperature on",
                null => "CPU temperature..."
            };
            _cpuTempKnopf.Enabled = cpuTemp is not null;

            SensorenZeigen(stand.Sensoren);
        }
        finally
        {
            _aktualisierungLaeuft = false;
        }
    }

    private void SensorenZeigen(IReadOnlyList<SensorZeile> sensoren)
    {
        // Nur die Werte aktualisieren, solange sich die Liste nicht ändert - // sonst flackert sie jede Sekunde und die Auswahl geht verloren.
        if (_sensoren.Items.Count == sensoren.Count)
        {
            bool gleich = true;
            for (int i = 0; i < sensoren.Count; i++)
            {
                if (_sensoren.Items[i].Text != sensoren[i].Id)
                {
                    gleich = false;
                    break;
                }
            }

            if (gleich)
            {
                for (int i = 0; i < sensoren.Count; i++)
                {
                    _sensoren.Items[i].SubItems[2].Text = sensoren[i].Wert;
                }

                return;
            }
        }

        _sensoren.BeginUpdate();
        _sensoren.Items.Clear();

        foreach (SensorZeile zeile in sensoren)
        {
            _sensoren.Items.Add(new ListViewItem([zeile.Id, zeile.Name, zeile.Wert]));
        }

        _sensoren.EndUpdate();
    }

    private void DienstSchalten(object? sender, EventArgs e)
    {
        if (_servicePfad is null)
        {
            return;
        }

        // Beenden läuft über die erhöhte Variante: Startet der Dienst über die
        // geplante Aufgabe, läuft er mit höchsten Privilegien, und ein Kill von
        // hier aus scheitert mit „Zugriff verweigert".
        //
        // Starten dagegen **nicht** erhöht: Der Dienst bekommt die Rechte
        // dieses Fensters. Wer ihn mit allen Rechten braucht, startet die
        // Oberfläche als Administrator - oder richtet den Autostart ein, der
        // ihn mit höchsten Privilegien startet. Das ist der vorgesehene Weg;
        // der Knopf hier ist zum Ausprobieren.
        //
        // Ob die Laufwerkstemperaturen wirklich erhöhte Rechte verlangen, ist
        // damit **nicht** geklärt: Ein Versuch ohne Erhöhung ist bislang nicht
        // gelaufen. Die Meldung unten behauptet deshalb nichts darüber.
        if (ServiceProbe.ProzessLaeuft())
        {
            if (ErhoehtAusfuehren(
                    Program.SchalterDienstBeenden,
                    () => (ServiceProbe.Beenden(), "The service could not be stopped."),
                    out string fehlerBeenden))
            {
                Melden("Service stopped.");
            }
            else
            {
                Melden(fehlerBeenden);
            }

            return;
        }

        try
        {
            ServiceProbe.Starten(_servicePfad);
            Melden("Service started, with this window's privileges. After a restart the API can take up to 15 seconds if the port is still in use.");
        }
        catch (Exception fehler)
        {
            Melden($"That did not work: {fehler.Message}");
        }
    }

    private void AutostartSchalten(object? sender, EventArgs e)
    {
        if (_servicePfad is null)
        {
            return;
        }

        // Hier bewusst frisch abfragen statt den Puffer zu nehmen: Es geht
        // gleich darum, etwas zu ändern.
        bool eingerichtet = TaskSetup.IstEingerichtet();

        if (!eingerichtet)
        {
            var frage = MessageBox.Show(
                this,
                $"""
                Es wird eine geplante Aufgabe „{TaskSetup.AufgabenName}" angelegt:

                • startet bei deiner Anmeldung
                • mit höchsten Privilegien, nötig für SMART und die HTTP-API
                • in deiner Benutzersitzung, nicht in Session 0
                • ohne Zeitlimit

                Gestartet wird:
                {_servicePfad}

                Windows fragt dafür gleich nach Administratorrechten. Mit „Aufgabe als XML anzeigen" kannst du vorher nachlesen, was genau eingetragen wird.
                """,
                "Autostart einrichten",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (frage != DialogResult.OK)
            {
                return;
            }
        }

        string schalter = eingerichtet ? Program.SchalterEntfernen : Program.SchalterEinrichten;
        string argumente = eingerichtet ? schalter : $"{schalter} \"{_servicePfad}\"";
        string pfad = _servicePfad;

        Func<(bool, string)> direkt = eingerichtet
            ? TaskSetup.Entfernen
            : () => TaskSetup.Einrichten(pfad);

        if (ErhoehtAusfuehren(argumente, direkt, out string fehler))
        {
            Melden(eingerichtet ? "Autostart entfernt." : "Autostart eingerichtet.");
        }
        else
        {
            Melden(fehler);
        }

        // Nach einer Änderung sofort nachsehen, nicht erst im nächsten Zyklus.
        _taktZaehler = int.MaxValue;
    }

    /// <summary>
    /// Ruft dieses Programm noch einmal mit erhöhten Rechten auf - nur für den
    /// einzelnen Vorgang. Das Fenster selbst läuft weiter ohne.
    /// </summary>
    /// <param name="argumente">Befehlszeile für den erhöhten Aufruf.</param>
    /// <param name="direkt">
    /// Derselbe Vorgang im eigenen Prozess, falls dieser schon erhöht läuft.
    /// So muss die Befehlszeile nicht wieder auseinandergenommen werden.
    /// </param>
    private static bool ErhoehtAusfuehren(string argumente, Func<(bool Erfolg, string Meldung)> direkt, out string fehler)
    {
        fehler = string.Empty;

        if (IstErhoeht())
        {
            (bool ok, string meldung) = direkt();

            if (!ok)
            {
                fehler = meldung;
            }

            return ok;
        }

        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? "DeckSense.Setup.exe", argumente)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            using Process? prozess = Process.Start(start);
            if (prozess is null)
            {
                fehler = "The operation could not be started.";
                return false;
            }

            prozess.WaitForExit(60_000);

            if (prozess.ExitCode != 0)
            {
                // Bewusst neutral: Über diesen Weg laufen inzwischen auch das
                // Installieren und das Schalten des Dienstes, nicht nur die
                // geplante Aufgabe.
                fehler = "The operation ended with an error. Is Windows running with restricted policies?";
                return false;
            }

            return true;
        }
        catch (Exception ausnahme)
        {
            // Häufigster Fall: Der Nutzer hat die Rückfrage von Windows abgelehnt.
            fehler = $"Cancelled: {ausnahme.Message}";
            return false;
        }
    }

    private static bool IstErhoeht()
    {
        using var identitaet = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identitaet).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Kopiert das Programm in den Zielordner. Danach sollte die dort
    /// installierte Fassung weiterverwendet werden - sie findet den Dienst
    /// neben sich und trägt denselben Pfad in die geplante Aufgabe ein.
    /// </summary>
    private void Installieren(object? sender, EventArgs e)
    {
        string ziel = _zielOrdner.Text.Trim();

        if (ziel.Length == 0)
        {
            Melden("Please enter a target folder.");
            return;
        }

        string quelle = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // In der Entwicklung liegen Dienst und Oberfläche in getrennten
        // Ausgabeordnern. Dann würde hier nur die halbe Anwendung kopiert.
        if (!File.Exists(Path.Combine(quelle, "DeckSense.Service.exe")))
        {
            MessageBox.Show(
                this,
                $"""
                DeckSense.Service.exe is missing from this application's folder:

                {quelle}

                Only the setup window would be copied. Publish both programs into one folder first:

                dotnet publish src\DeckSense.Service -c Release -o artifacts\DeckSense
                dotnet publish src\DeckSense.Setup   -c Release -o artifacts\DeckSense

                then start DeckSense.Setup.exe from that folder.
                """,
                "Incomplete source folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // Vor der Rückfrage und vor der UAC-Abfrage: Aus dem installierten
        // Ordner heraus gestartet, sind Quelle und Ziel derselbe Ordner. Der
        // Installer fängt das ab, aber erst im erhöhten Lauf - eine
        // Windows-Rückfrage für etwas, das nicht gehen kann.
        if (string.Equals(
                Path.GetFullPath(quelle).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ziel).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                $"""
                Source and target are the same folder:

                {quelle}

                This copy already runs from the target folder, so there is nothing to copy.

                To update, start DeckSense.Setup.exe from the newly built folder, not this one:

                dotnet publish src\DeckSense.Service -c Release -o artifacts\DeckSense
                dotnet publish src\DeckSense.Setup   -c Release -o artifacts\DeckSense
                artifacts\DeckSense\DeckSense.Setup.exe
                """,
                "Nothing to copy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        bool aktualisierung = Installer.IstInstalliert(ziel);

        var frage = MessageBox.Show(
            this,
            $"""
            {(aktualisierung ? "An existing copy will be updated" : "The program will be copied")}:

            from  {quelle}
            to    {ziel}

            An existing appsettings.json is kept, and the "logs" folder is not copied along.

            If the service is running it is stopped and then restarted from the target folder, because its executable cannot be overwritten while it runs.

            Windows will ask for administrator rights in a moment, because "Program Files" is protected.
            """,
            aktualisierung ? "Update program" : "Install program",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (frage != DialogResult.OK)
        {
            return;
        }

        string argumente = $"{Program.SchalterInstallieren} \"{quelle}\" \"{ziel}\"";

        if (!ErhoehtAusfuehren(argumente, () =>
        {
            Installer.Ergebnis r = Installer.Kopieren(quelle, ziel);
            return (r.Erfolg, r.Meldung);
        }, out string fehler))
        {
            Melden(fehler);
            return;
        }

        string installiertesSetup = Path.Combine(ziel, "DeckSense.Setup.exe");

        var weiter = MessageBox.Show(
            this,
            $"""
            Copied to {ziel}.

            Set up the autostart from the installed copy, not from here: only that one writes the local path into the task instead of the one you started from.

            Open the installed copy now and close this window?
            """,
            "Installed",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (weiter == DialogResult.Yes && File.Exists(installiertesSetup))
        {
            try
            {
                Process.Start(new ProcessStartInfo(installiertesSetup) { UseShellExecute = true });
                Close();
            }
            catch (Exception ausnahme)
            {
                Melden($"Could not open the installed copy: {ausnahme.Message}");
            }
        }
        else
        {
            Melden($"Copied to {ziel}.");
        }
    }

    /// <summary>
    /// Schaltet die CPU-Temperatur frei - der einzige Wert in DeckSense, der
    /// einen Kernel-Treiber voraussetzt. Deshalb nicht stillschweigend, sondern
    /// mit dem, was über Anti-Cheat tatsächlich bekannt ist. Siehe
    /// Entscheidung 0023.
    /// </summary>
    private void CpuTempSchalten(object? sender, EventArgs e)
    {
        string? pfad = CpuTempSwitch.Pfad(_servicePfad);
        bool? stand = CpuTempSwitch.Lesen(pfad);

        if (stand is null)
        {
            Melden("appsettings.json was not found. Has the service ever run?");
            return;
        }

        if (stand == true)
        {
            // Ausschalten braucht keine Warnung.
            (bool okAus, string meldungAus) = CpuTempSwitch.Setzen(pfad, false);
            Melden(okAus
                ? meldungAus + " PawnIO stays installed; you can remove it from the Windows settings."
                : meldungAus);
            _taktZaehler = int.MaxValue;
            return;
        }

        string pawnio = CpuTempSwitch.PawnIoInstalliert
            ? $"PawnIO is installed, version {CpuTempSwitch.PawnIoVersion}."
            : "PawnIO is NOT installed on this machine. Without it the value stays unavailable.";

        var frage = MessageBox.Show(
            this,
            $"""
            CPU temperature is the only value in DeckSense that needs a kernel driver: PawnIO, which LibreHardwareMonitor uses to read the MSRs. AMD does not expose Tctl/Tdie from user mode, so without the driver there is no value.

            What is known about anti-cheat, as of February 2026:

            * FACEIT Anti-Cheat blocks PawnIO. According to FACEIT support this is because of the signing certificate, which was also used to sign cheat software. Consequence: the game does not start. No bans are known.

            * Battlefield 6 (Javelin) works with it, reported by users and by the LibreHardwareMonitor maintainer.

            * For Riot Vanguard (Valorant, League of Legends), Easy Anti-Cheat and BattlEye there are no reports at all. Neither that they block it, nor that it works. That means untested, not harmless.

            In the worst known case a game refuses to start while the driver is installed. You can undo this at any time: turn this switch back off and uninstall PawnIO from the Windows settings.

            {pawnio}

            DeckSense does not install PawnIO. That stays your decision and your step.

            Turn the switch on now?
            """,
            "CPU temperature needs a kernel driver",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (frage != DialogResult.OK)
        {
            return;
        }

        (bool ok, string meldung) = CpuTempSwitch.Setzen(pfad, true);

        if (!ok)
        {
            Melden(meldung);
            return;
        }

        Melden(CpuTempSwitch.PawnIoInstalliert
            ? meldung + " Restart the service, then cpu.temp delivers values."
            : meldung + " PawnIO is still missing; without it the value stays unavailable.");

        _taktZaehler = int.MaxValue;
    }

    private void XmlZeigen(object? sender, EventArgs e)
    {
        if (_servicePfad is null)
        {
            return;
        }

        using var fenster = new Form
        {
            Text = "Scheduled task as XML",
            BackColor = Grund,
            ForeColor = Schrift,
            ClientSize = new Size(700, 560),
            StartPosition = FormStartPosition.CenterParent,
        };

        var feld = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            BackColor = Flaeche,
            ForeColor = Schrift,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
            Text = TaskSetup.BuildXml(_servicePfad).Replace("\n", Environment.NewLine),
        };

        fenster.Controls.Add(feld);
        fenster.ShowDialog(this);
    }

    private void Melden(string text)
    {
        _meldung.Text = text;
        _meldung.ForeColor = Violett;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _takt.Stop();
            _takt.Dispose();
        }

        base.Dispose(disposing);
    }
}
