window.appInterop = {
  getLocalStorageItem(key) {
    return window.localStorage.getItem(key);
  },
  setLocalStorageItem(key, value) {
    window.localStorage.setItem(key, value);
  },
  removeLocalStorageItem(key) {
    window.localStorage.removeItem(key);
  },
  startResize(startX, currentWidth, dotNetRef) {
    function onMove(e) {
      const w = Math.min(600, Math.max(150, currentWidth + (e.clientX - startX)));
      dotNetRef.invokeMethodAsync('OnResizeDrag', w);
    }
    function onUp(e) {
      const w = Math.min(600, Math.max(150, currentWidth + (e.clientX - startX)));
      dotNetRef.invokeMethodAsync('OnResizeDrop', w);
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.body.style.userSelect = '';
      document.body.style.cursor = '';
    }
    document.body.style.userSelect = 'none';
    document.body.style.cursor = 'ew-resize';
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  },
  isMobile() {
    return window.matchMedia('(max-width: 767px)').matches;
  },
  startMobileWatcher(dotNetRef) {
    const mql = window.matchMedia('(max-width: 767px)');
    mql.addEventListener('change', e => {
      dotNetRef.invokeMethodAsync('SetMobile', e.matches);
    });
  },
  scrollToBottom(element) {
    if (element instanceof Element) element.scrollTop = element.scrollHeight;
  }
};
