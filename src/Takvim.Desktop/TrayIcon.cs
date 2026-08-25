using System.Drawing;
using System.Windows.Forms;
using Takvim.Data.Services;

namespace Takvim.Desktop;

/// <summary>
/// Sistem tepsisindeki simge ve Windows bildirimleri.
/// <para>
/// WPF'in kendi tepsi desteği yok; Windows Forms'un <see cref="NotifyIcon"/>
/// sınıfı kullanılır. Bildirim için ayrı bir paket eklenmedi: balon ipucu
/// Windows 10 ve 11'de zaten modern bildirim olarak çizilir ve uygulamanın
/// bağımlılık listesini büyütmez — bu makinede imzasız her yeni derleme
/// Smart App Control'e takılma riski taşıdığı için bu ayrıca önemli.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    /// <summary>Kullanıcı pencereyi görmek istediğinde.</summary>
    public event Action? OpenRequested;

    /// <summary>Kullanıcı uygulamadan gerçekten çıkmak istediğinde.</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Takvimi aç", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Takvim",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Çift tıklama en sık beklenen davranış; menüye gitmeye gerek kalmasın.
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>
    /// Hatırlatıcıyı bildirim olarak gösterir.
    /// <para>
    /// Birden çok hatırlatıcı aynı anda çaldığında tek bildirim çıkar: üst üste
    /// beş bildirim göstermek, hiçbirini okunmaz kılar.
    /// </para>
    /// </summary>
    public void ShowReminders(IReadOnlyList<DueReminder> reminders)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        if (reminders.Count == 0) return;

        var first = reminders[0];

        var title = reminders.Count == 1
            ? Describe(first)
            : $"{reminders.Count} etkinlik yaklaşıyor";

        var body = reminders.Count == 1
            ? DetailOf(first)
            : string.Join(", ", reminders.Take(3).Select(r => r.Title));

        _icon.ShowBalloonTip(10_000, title, body, ToolTipIcon.Info);
    }

    /// <summary>Pencere tepsiye indiğinde bir kez gösterilen bilgi.</summary>
    public void ShowHiddenHint()
        => _icon.ShowBalloonTip(
            5_000,
            "Takvim arka planda çalışıyor",
            "Hatırlatıcılar gelmeye devam eder. Açmak için simgeye çift tıklayın.",
            ToolTipIcon.Info);

    public void Dispose()
    {
        // Görünürlük önce kapatılır; aksi hâlde simge tepside asılı kalabiliyor.
        _icon.Visible = false;
        _icon.Dispose();
    }

    // ==================================================================

    /// <summary>"14:30 Bütçe toplantısı" gibi tek satırlık başlık.</summary>
    private static string Describe(DueReminder reminder)
        => reminder.IsAllDay
            ? reminder.Title
            : $"{reminder.StartLocal.Hour:00}:{reminder.StartLocal.Minute:00} {reminder.Title}";

    private static string DetailOf(DueReminder reminder)
    {
        var when = reminder.MinutesBefore switch
        {
            0 => "Şimdi başlıyor",
            < 60 => $"{reminder.MinutesBefore} dakika içinde",
            < 24 * 60 => $"{reminder.MinutesBefore / 60} saat içinde",
            _ => $"{reminder.MinutesBefore / (24 * 60)} gün içinde",
        };

        return string.IsNullOrWhiteSpace(reminder.LocationText)
            ? when
            : $"{when} · {reminder.LocationText}";
    }

    /// <summary>
    /// Pencerenin simgesi uygulamayla birlikte gelmiyor; tepsi için basit bir
    /// simge çizilir. Böylece ayrıca bir .ico dosyası taşımak gerekmez.
    /// </summary>
    private static Icon LoadIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(0x2E, 0x7D, 0xF6));
            using var paper = new SolidBrush(Color.White);

            // Takvim yaprağı: gövde, üst şerit ve iki halka.
            graphics.FillRectangle(body, 2, 6, 28, 24);
            graphics.FillRectangle(paper, 5, 13, 22, 14);
            graphics.FillRectangle(body, 9, 2, 3, 7);
            graphics.FillRectangle(body, 20, 2, 3, 7);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
