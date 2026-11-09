// Routes browser-only features through NativeBridge (MauiProgram's assembly). Load after composer.js.
(() => {
    const call = (method, ...args) => DotNet.invokeMethodAsync('SocialPoster.MAUI', method, ...args);
    const isExternal = href => /^https?:/i.test(href) && new URL(href, location.href).origin !== location.origin;

    // Post links, X intents and LinkedIn "Tag by hand" open in the system browser instead of a web view pop-up.
    window.socialPoster.openInNewTab = url => call('OpenInBrowser', url);
    window.socialPoster.copy = text => call('CopyText', text);
    // Web views don't reliably handle blob downloads (WKWebView ignores them), so write the file natively.
    window.socialPoster.downloadFile = (fileName, text) => call('SaveToDownloads', fileName, text);

    // target="_blank" links (View post, profile links) do nothing in WKWebView, so open them natively too.
    document.addEventListener('click', e => {
        const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
        if (!a || e.defaultPrevented || !isExternal(a.href)) return;
        e.preventDefault();
        call('OpenInBrowser', a.href);
    }, true);
})();
