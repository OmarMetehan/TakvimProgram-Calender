// Takvim — tarayıcı tarafı yardımcıları.
// Blazor'ın erişemediği üç şey burada toplanır: klavye olayları, kaydırma konumu
// ve yerel depolama. Başka mantık buraya konmaz.

window.takvim = (function () {
    'use strict';

    let shortcutHandler = null;

    /** Metin girilen bir alanda mıyız? Kısayollar orada devre dışı kalmalı. */
    function isTyping(target) {
        if (!target) return false;
        const tag = target.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || target.isContentEditable;
    }

    document.addEventListener('keydown', function (e) {
        if (!shortcutHandler) return;

        // Tarayıcının kendi kısayolları (Ctrl+F, Ctrl+P...) engellenmez.
        if (e.ctrlKey || e.metaKey || e.altKey) return;

        // Escape her yerde çalışır; diğer kısayollar yazarken çalışmaz.
        if (e.key !== 'Escape' && isTyping(e.target)) return;

        shortcutHandler.invokeMethodAsync('HandleKey', e.key, e.shiftKey)
            .then(function (handled) {
                // Boşluk ve "/" gibi tuşlar sayfayı kaydırır; işlendiyse engellenir.
                if (handled) e.preventDefault();
            })
            .catch(function () { /* devre kapanmış olabilir */ });
    });

    return {
        /** Kısayolları alacak .NET nesnesini kaydeder. */
        registerShortcuts: function (dotNetRef) {
            shortcutHandler = dotNetRef;
        },

        unregisterShortcuts: function () {
            shortcutHandler = null;
        },

        /**
         * Izgarayı verilen orana kaydırır (0-1). Geçerli saati görünür kılmak için
         * kullanılır; ekranın ortasına denk gelecek biçimde konumlanır.
         */
        scrollToRatio: function (elementId, ratio) {
            const el = document.getElementById(elementId);
            if (!el) return;

            const target = el.scrollHeight * ratio - el.clientHeight / 2;
            el.scrollTop = Math.max(0, target);
        },

        /**
         * Kaydırma çubuğunun genişliğini ölçüp CSS değişkenine yazar.
         * Böylece ızgara başlıkları alttaki sütunlarla hizalı kalır.
         */
        measureScrollbar: function (elementId) {
            const el = document.getElementById(elementId);
            if (!el) return 0;

            const width = el.offsetWidth - el.clientWidth;
            document.documentElement.style.setProperty('--kaydirma-genisligi', width + 'px');
            return width;
        },

        /** Temayı değiştirir ve seçimi saklar. */
        setTheme: function (theme) {
            document.documentElement.setAttribute('data-tema', theme);
            try { localStorage.setItem('takvim-tema', theme); } catch (e) { /* özel pencere */ }
        },

        getTheme: function () {
            return document.documentElement.getAttribute('data-tema') || 'acik';
        },

        /** Yoğunluk ayarını belge köküne yazar; CSS satır yüksekliğini oradan okur. */
        setDensity: function (density) {
            document.documentElement.setAttribute('data-yogunluk', density);
        },

        focus: function (selector) {
            const el = document.querySelector(selector);
            if (el) el.focus();
        },

        print: function () {
            window.print();
        },

        /** Oluşturulan ICS metnini dosya olarak indirir. */
        downloadText: function (fileName, text, mimeType) {
            const blob = new Blob([text], { type: mimeType || 'text/plain;charset=utf-8' });
            const url = URL.createObjectURL(blob);

            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);

            // Nesne URL'i hemen serbest bırakılmazsa bellek sızdırır.
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        },

        /**
         * İkili bir dosyayı base64'ten çözüp indirir. Ekler bu yolla gelir:
         * uygulama gömülü çalıştığı için ayrı bir indirme uç noktası yok.
         */
        downloadBase64: function (fileName, base64, mimeType) {
            const binary = atob(base64);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);

            const url = URL.createObjectURL(new Blob([bytes], { type: mimeType || 'application/octet-stream' }));

            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);

            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        },

        /**
         * Metni panoya kopyalar. Pano izni verilmemişse gizli bir alan
         * üzerinden eski yönteme düşer; gömülü pencerede izin istemi çıkmaz.
         */
        copyText: async function (text) {
            try {
                await navigator.clipboard.writeText(text);
                return true;
            } catch (e) {
                const field = document.createElement('textarea');
                field.value = text;
                field.style.position = 'fixed';
                field.style.opacity = '0';
                document.body.appendChild(field);
                field.select();

                let copied = false;
                try { copied = document.execCommand('copy'); } catch (e2) { copied = false; }

                document.body.removeChild(field);
                return copied;
            }
        },

        /** Seçilen dosyanın metnini okur; ICS içe aktarma bunu kullanır. */
        readFileText: async function (inputElement) {
            if (!inputElement || !inputElement.files || inputElement.files.length === 0) return null;
            return await inputElement.files[0].text();
        },

        /**
         * Bir öğenin ekrandaki yerini döner. Önizleme kartı, tıklanan etkinliğin
         * yanına yerleştirilirken bu ölçüyü kullanır.
         */
        getRect: function (element) {
            if (!element) return null;
            const r = element.getBoundingClientRect();
            return {
                top: r.top, left: r.left, right: r.right, bottom: r.bottom,
                width: r.width, height: r.height,
                viewportWidth: window.innerWidth, viewportHeight: window.innerHeight
            };
        }
    };
})();
