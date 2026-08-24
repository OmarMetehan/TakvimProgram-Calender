using Takvim.Core.Text;

namespace Takvim.Core.Tests;

/// <summary>
/// Biçimli metnin HTML'e çevrilmesi. Buradaki en önemli sınamalar biçim
/// değil <b>kaçışlama</b> sınamalarıdır: çıktı doğrudan sayfaya gömüldüğü
/// için kullanıcı metninin hiçbir koşulda etiket üretmemesi gerekir.
/// </summary>
public class RichTextTests
{
    // ==================================================================
    // Kaçışlama
    // ==================================================================

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src='evil'></iframe>")]
    [InlineData("<a href=\"javascript:alert(1)\">tık</a>")]
    [InlineData("<style>body{display:none}</style>")]
    public void Kullanici_etiketi_uretemez(string source)
    {
        var html = RichText.ToHtml(source);

        // Metnin kendisi çıktıda kalabilir — "onerror" yazan bir cümle zararsızdır.
        // Aranan şey, kullanıcı metninden <b>etiket</b> doğmamasıdır: üretilen tüm
        // etiketler bizim beyaz listemizden gelmelidir.
        foreach (var tag in Tags(html))
        {
            Assert.Contains(tag, Allowed);
        }

        Assert.Contains("&lt;", html, StringComparison.Ordinal);
    }

    /// <summary>RichText'in üretebileceği etiketlerin tamamı.</summary>
    private static readonly string[] Allowed =
        ["p", "br", "strong", "em", "del", "code", "ul", "ol", "li", "h3", "h4", "h5", "blockquote", "a"];

    /// <summary>Çıktıdaki etiket adlarını çıkarır.</summary>
    private static IEnumerable<string> Tags(string html)
    {
        var index = 0;

        while ((index = html.IndexOf('<', index)) >= 0)
        {
            var end = html.IndexOfAny([' ', '>'], index);
            if (end < 0) yield break;

            yield return html[index..end].TrimStart('<').TrimStart('/');
            index = end;
        }
    }

    [Fact]
    public void Acili_parantez_kacislanir()
        => Assert.Equal("<p>5 &lt; 7 &amp; 7 &gt; 5</p>", RichText.ToHtml("5 < 7 & 7 > 5"));

    [Fact]
    public void Tirnak_kacislanir()
        => Assert.Contains("&quot;", RichText.ToHtml("\"alıntı\""), StringComparison.Ordinal);

    [Fact]
    public void Bicim_isaretinin_icindeki_metin_de_kacislanir()
        => Assert.Equal(
            "<p><strong>&lt;b&gt;</strong></p>",
            RichText.ToHtml("**<b>**"));

    // ==================================================================
    // Satır içi biçimler
    // ==================================================================

    [Fact]
    public void Kalin_ve_egik_ayrilir()
    {
        Assert.Equal("<p><strong>kalın</strong></p>", RichText.ToHtml("**kalın**"));
        Assert.Equal("<p><em>eğik</em></p>", RichText.ToHtml("*eğik*"));
    }

    [Fact]
    public void Kalin_egikten_once_islenir()
        => Assert.Equal("<p><strong>ikisi de</strong></p>", RichText.ToHtml("**ikisi de**"));

    [Fact]
    public void Tek_kalan_isaret_metinde_kalir()
    {
        // "5 * 3" yazan biri eğik yazı istememiştir.
        Assert.Equal("<p>5 * 3 = 15</p>", RichText.ToHtml("5 * 3 = 15"));
    }

    [Fact]
    public void Ustu_cizili_ve_kod()
    {
        Assert.Equal("<p><del>iptal</del></p>", RichText.ToHtml("~~iptal~~"));
        Assert.Equal("<p><code>dotnet build</code></p>", RichText.ToHtml("`dotnet build`"));
    }

    // ==================================================================
    // Satır türleri
    // ==================================================================

    [Fact]
    public void Madde_listesi_uretilir()
        => Assert.Equal(
            "<ul><li>bir</li><li>iki</li></ul>",
            RichText.ToHtml("- bir\n- iki"));

    [Fact]
    public void Numarali_liste_uretilir()
        => Assert.Equal(
            "<ol><li>bir</li><li>iki</li></ol>",
            RichText.ToHtml("1. bir\n2. iki"));

    [Fact]
    public void Liste_turu_degisince_yeni_liste_acilir()
        => Assert.Equal(
            "<ul><li>bir</li></ul><ol><li>iki</li></ol>",
            RichText.ToHtml("- bir\n1. iki"));

    [Fact]
    public void Basliklar_h3ten_baslar()
    {
        // Metin sayfanın içine gömülür; h1 sayfanın kendi başlığıdır.
        Assert.Equal("<h3>Büyük</h3>", RichText.ToHtml("# Büyük"));
        Assert.Equal("<h4>Orta</h4>", RichText.ToHtml("## Orta"));
        Assert.Equal("<h5>Küçük</h5>", RichText.ToHtml("### Küçük"));
    }

    [Fact]
    public void Alinti_uretilir()
        => Assert.Equal("<blockquote>söylenen</blockquote>", RichText.ToHtml("> söylenen"));

    [Fact]
    public void Ardisik_satirlar_tek_paragrafta_birlesir()
        => Assert.Equal("<p>bir<br>iki</p>", RichText.ToHtml("bir\niki"));

    [Fact]
    public void Bos_satir_paragrafi_ayirir()
        => Assert.Equal("<p>bir</p><p>iki</p>", RichText.ToHtml("bir\n\niki"));

    [Fact]
    public void Windows_satir_sonu_da_calisir()
        => Assert.Equal("<p>bir<br>iki</p>", RichText.ToHtml("bir\r\niki"));

    [Fact]
    public void Bos_girdi_bos_doner()
    {
        Assert.Equal(string.Empty, RichText.ToHtml(null));
        Assert.Equal(string.Empty, RichText.ToHtml("   "));
    }

    // ==================================================================
    // Bağlantılar
    // ==================================================================

    [Fact]
    public void Adres_baglantiya_cevrilir()
    {
        var html = RichText.ToHtml("Toplantı: https://meet.jit.si/abc");

        Assert.Contains("<a href=\"https://meet.jit.si/abc\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Adresin_sonundaki_noktalama_baglantiya_girmez()
    {
        var html = RichText.ToHtml("Bak https://ornek.com/sayfa.");

        Assert.Contains("href=\"https://ornek.com/sayfa\"", html, StringComparison.Ordinal);
        Assert.EndsWith(".</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Iki_adres_de_baglanir()
    {
        var html = RichText.ToHtml("http://bir.com ve http://iki.com");

        Assert.Equal(2, html.Split("<a href=", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Adres_tirnakta_biter()
    {
        // Tırnak kaçışlanmış olduğu için niteliği zaten bozamaz, ama adresin
        // parçası da sayılmamalı: bağlantı orada biter, kalanı düz metindir.
        var html = RichText.ToHtml("https://ornek.com/\"onmouseover=alert(1)");

        Assert.Contains("href=\"https://ornek.com/\"", html, StringComparison.Ordinal);
        Assert.Contains("</a>&quot;onmouseover=alert(1)", html, StringComparison.Ordinal);
    }

    // ==================================================================
    // HTML kaynaklı metin
    // ==================================================================

    [Fact]
    public void Disaridan_gelen_html_once_duz_metne_indirilir()
    {
        // CalDAV ile gelen açıklamalar HTML içerebilir; etiketler ekranda
        // görünmemeli.
        var html = RichText.ToHtml("<p>Merhaba <b>dünya</b></p>");

        Assert.DoesNotContain("&lt;p&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Merhaba", html, StringComparison.Ordinal);
        Assert.Contains("dünya", html, StringComparison.Ordinal);
    }

    // ==================================================================
    // Düz metin
    // ==================================================================

    [Fact]
    public void Duz_metin_isaretleri_atar()
        => Assert.Equal("kalın ve eğik", RichText.ToPlainText("**kalın** ve *eğik*"));

    [Fact]
    public void Duz_metinde_madde_imi_korunur()
        => Assert.Equal("• bir\r\n• iki", RichText.ToPlainText("- bir\n2. iki").ReplaceLineEndings("\r\n"));
}
