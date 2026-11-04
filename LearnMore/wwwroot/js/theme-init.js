(function () {
    'use strict';
    let dark = false;
    try { dark = localStorage.getItem('theme') === 'dark-theme'; } catch { }

    function apply() {
        document.documentElement.classList.toggle('dark-theme', dark);
        document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
        if (document.body) document.body.classList.toggle('dark-theme', dark);
    }

    window.LearnMoreTheme = {
        applyToBody: apply,
        toggle: function () {
            dark = !dark;
            apply();
            try { localStorage.setItem('theme', dark ? 'dark-theme' : ''); } catch { }
        }
    };
    apply();
    window.addEventListener('storage', function (event) {
        if (event.key !== 'theme') return;
        dark = event.newValue === 'dark-theme';
        apply();
    });
})();
