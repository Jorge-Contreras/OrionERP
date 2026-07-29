(function () {
    "use strict";

    const registrations = new WeakMap();

    const getSafePreferences = (value) => ({
        favorites: Array.isArray(value?.favorites)
            ? value.favorites.filter((item) => typeof item === "string")
            : [],
        recent: Array.isArray(value?.recent)
            ? value.recent.filter((item) => typeof item === "string")
            : []
    });

    window.orionCommandCenter = {
        initialize(dialog, searchInput, dotNetReference) {
            if (!dialog || registrations.has(dialog)) {
                return;
            }

            const onDocumentKeyDown = (event) => {
                const isCommandShortcut = (event.ctrlKey || event.metaKey)
                    && !event.altKey
                    && event.key.toLowerCase() === "k";

                if (!isCommandShortcut) {
                    return;
                }

                event.preventDefault();

                if (dialog.open) {
                    searchInput?.focus({ preventScroll: true });
                    searchInput?.select();
                    return;
                }

                void dotNetReference.invokeMethodAsync("OpenFromShortcut");
            };

            const onDialogClose = () => {
                void dotNetReference.invokeMethodAsync("HandleDialogClosed");
            };

            const onDialogClick = (event) => {
                if (event.target !== dialog) {
                    return;
                }

                const bounds = dialog.getBoundingClientRect();
                const isInside = event.clientX >= bounds.left
                    && event.clientX <= bounds.right
                    && event.clientY >= bounds.top
                    && event.clientY <= bounds.bottom;

                if (!isInside) {
                    dialog.close();
                }
            };

            document.addEventListener("keydown", onDocumentKeyDown);
            dialog.addEventListener("close", onDialogClose);
            dialog.addEventListener("click", onDialogClick);

            registrations.set(dialog, {
                onDocumentKeyDown,
                onDialogClose,
                onDialogClick
            });
        },

        open(dialog, searchInput) {
            if (!dialog) {
                return;
            }

            if (!dialog.open) {
                dialog.showModal();
            }

            window.requestAnimationFrame(() => {
                searchInput?.focus({ preventScroll: true });
                searchInput?.select();
            });
        },

        close(dialog) {
            if (dialog?.open) {
                dialog.close();
            }
        },

        focusSearch(searchInput) {
            window.requestAnimationFrame(() => {
                searchInput?.focus({ preventScroll: true });
            });
        },

        scrollActive(dialog, elementId) {
            if (!dialog || !elementId) {
                return;
            }

            window.requestAnimationFrame(() => {
                dialog.querySelector(`#${CSS.escape(elementId)}`)
                    ?.scrollIntoView({ block: "nearest", inline: "nearest" });
            });
        },

        loadPreferences(storageKey) {
            try {
                const rawValue = window.localStorage.getItem(storageKey);
                return rawValue
                    ? getSafePreferences(JSON.parse(rawValue))
                    : getSafePreferences(null);
            } catch {
                return getSafePreferences(null);
            }
        },

        savePreferences(storageKey, preferences) {
            try {
                window.localStorage.setItem(
                    storageKey,
                    JSON.stringify(getSafePreferences(preferences)));
            } catch {
                // Preferences are an enhancement; navigation remains fully functional.
            }
        },

        dispose(dialog) {
            const registration = dialog ? registrations.get(dialog) : null;
            if (!registration) {
                return;
            }

            document.removeEventListener("keydown", registration.onDocumentKeyDown);
            dialog.removeEventListener("close", registration.onDialogClose);
            dialog.removeEventListener("click", registration.onDialogClick);
            registrations.delete(dialog);
        }
    };
})();
