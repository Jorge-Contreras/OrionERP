(() => {
  const guardedKeysAttribute = "data-orion-prevent-keys";
  const comboboxKeys = new Set(["Enter", "ArrowDown", "ArrowUp", "Escape"]);

  const getConfiguredKeys = (input) => new Set(
    (input.getAttribute(guardedKeysAttribute) ?? "")
      .split(/\s+/)
      .filter(Boolean));

  document.addEventListener("keydown", (event) => {
    if (event.defaultPrevented || event.isComposing || !(event.target instanceof HTMLInputElement)) {
      return;
    }

    const input = event.target;
    const isSearchSubmit = input.type === "search" && event.key === "Enter";
    const isComboboxNavigation = input.getAttribute("role") === "combobox" && comboboxKeys.has(event.key);
    const isConfiguredKey = getConfiguredKeys(input).has(event.key);

    if (isSearchSubmit || isComboboxNavigation || isConfiguredKey) {
      event.preventDefault();
    }
  }, true);
})();
