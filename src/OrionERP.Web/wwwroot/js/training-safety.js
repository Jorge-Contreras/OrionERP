const blockedMessage = "El entorno de capacitación bloquea la impresión, ubicación, cámara y dispositivos reales.";

function rejectBlockedAction() {
    return Promise.reject(new Error(blockedMessage));
}

function throwBlockedAction() {
    throw new Error(blockedMessage);
}

export function installTrainingSafetyGuards() {
    if (window.__orionTrainingSafetyInstalled) {
        return;
    }

    window.__orionTrainingSafetyInstalled = true;
    document.documentElement.dataset.orionEnvironment = "training";

    // Blazor pages that invoke the browser print function are blocked here.
    // Restaurant PDF printing and QZ/cash-drawer operations are also replaced
    // before they can reach a local device.
    window.print = throwBlockedAction;
    window.orionPrintReport = throwBlockedAction;
    window.orionPrintBalanza = throwBlockedAction;

    if (window.restaurantUi) {
        window.restaurantUi.openCashDrawer = rejectBlockedAction;
        window.restaurantUi.printPdf = rejectBlockedAction;
    }

    // Do not collect real location or camera evidence in the disposable
    // training database. Existing workflows receive a standard permission-
    // denied shape and can present their normal validation feedback.
    if (navigator.geolocation) {
        const denyLocation = (_success, error) => error?.({
            code: 1,
            message: blockedMessage,
            PERMISSION_DENIED: 1
        });
        navigator.geolocation.getCurrentPosition = denyLocation;
        navigator.geolocation.watchPosition = (_success, error) => {
            denyLocation(_success, error);
            return 0;
        };
        navigator.geolocation.clearWatch = () => {};
    }

    if (navigator.mediaDevices?.getUserMedia) {
        navigator.mediaDevices.getUserMedia = rejectBlockedAction;
    }

    document.addEventListener("click", (event) => {
        const input = event.target instanceof Element
            ? event.target.closest('input[type="file"][capture]')
            : null;
        if (!input) {
            return;
        }

        event.preventDefault();
        window.alert(blockedMessage);
    }, true);
}
