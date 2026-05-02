(function () {
    function showBlocked() {
        var app = document.getElementById('app');
        if (!app) return;
        app.innerHTML =
            '<div style="display:flex;align-items:center;justify-content:center;' +
            'height:100vh;font-family:system-ui,sans-serif;background:#f8fafc">' +
            '<div style="text-align:center;padding:2.5rem 2rem;max-width:380px;background:#fff;' +
            'border-radius:16px;box-shadow:0 20px 60px rgba(15,23,42,0.12);border:1px solid #e5e7eb">' +
            '<div style="font-size:3rem;margin-bottom:1rem">&#128222;</div>' +
            '<h2 style="margin:0 0 0.5rem;color:#0f172a;font-size:1.25rem;font-weight:700">Already open</h2>' +
            '<p style="margin:0 0 1.5rem;color:#64748b;font-size:0.9rem;line-height:1.5">' +
            'WebPhone is already running in another browser tab.</p>' +
            '<button onclick="window.close()" style="background:linear-gradient(135deg,#2563eb,#1d4ed8);' +
            'color:#fff;border:none;border-radius:999px;padding:0.55rem 1.5rem;font-size:0.9rem;' +
            'font-weight:600;cursor:pointer;transition:opacity 0.15s" ' +
            'onmouseover="this.style.opacity=\'0.85\'" onmouseout="this.style.opacity=\'1\'">' +
            'Close this tab</button>' +
            '</div></div>';
    }

    window.tabGuard = {
        check: function () {
            if (!navigator || !navigator.locks) return Promise.resolve(true);
            return new Promise(function (resolve) {
                navigator.locks.request('webphone-tab', { ifAvailable: true }, function (lock) {
                    resolve(!!lock);
                    // Hold the lock for as long as this tab lives
                    if (lock) return new Promise(function () {});
                    return Promise.resolve();
                });
            });
        },
        showBlocked: showBlocked
    };
})();
