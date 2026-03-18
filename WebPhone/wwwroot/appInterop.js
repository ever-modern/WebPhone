window.appInterop = {
  getLocalStorageItem(key) {
    return window.localStorage.getItem(key);
  },
  setLocalStorageItem(key, value) {
    window.localStorage.setItem(key, value);
  },
  removeLocalStorageItem(key) {
    window.localStorage.removeItem(key);
  }
};
