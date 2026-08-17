<script>
    (function () {
        const zoom = () => {
            const f = document.querySelector('.frame-16-9');
            if (!f) return;
            const [fw, fh] = [f.clientWidth, f.clientHeight];
            document.querySelectorAll('.slide').forEach(s => {
                s.style.transform = s.style.width = s.style.height = '';
                const [sw, sh] = [s.scrollWidth, s.scrollHeight];
                if (sh > fh || sw > fw) {
                    const z = Math.min(fw / sw, fh / sh) * 0.8;
                    s.style.cssText += `transform:scale(${z});transform-origin:top left;width:${100 / z}%;height:${100 / z}%;`;
                }
            });
        };

        // All'avvio e dopo il caricamento
        zoom();
        window.addEventListener('load', zoom);

        // Eventi
        let t;
        window.addEventListener('resize', () => (clearTimeout(t), t = setTimeout(zoom, 200)));
        document.querySelectorAll('.slide').forEach(s => {
            const o = new MutationObserver(() => setTimeout(zoom, 150));
            o.observe(s, { attributes: true, attributeFilter: ['class'] });
        });
    })();
</script>