(() => {
    const toggle = document.getElementById('navigation-toggle');
    const navigation = document.getElementById('sample-navigation');
    if (!toggle || !navigation) return;

    function setOpen(open) {
        document.body.classList.toggle('navigation-open', open);
        toggle.setAttribute('aria-expanded', String(open));
        toggle.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
    }

    toggle.addEventListener('click', () => {
        setOpen(toggle.getAttribute('aria-expanded') !== 'true');
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && toggle.getAttribute('aria-expanded') === 'true') {
            setOpen(false);
            toggle.focus();
        }
    });
    navigation.addEventListener('click', event => {
        if (event.target.closest('a')) setOpen(false);
    });
})();
