let restaurantQzSecurityConfiguration = null;

const configureRestaurantQzSecurity = function (qzClient) {
    if (restaurantQzSecurityConfiguration) {
        return restaurantQzSecurityConfiguration;
    }

    restaurantQzSecurityConfiguration = (async () => {
        const certificateResponse = await window.fetch("/api/restaurant/qz/certificate", {
            cache: "no-store",
            credentials: "same-origin",
            headers: {
                "Accept": "text/plain",
                "X-Requested-With": "XMLHttpRequest"
            }
        });
        if (!certificateResponse.ok) {
            throw new Error(
                `OrionERP no pudo cargar el certificado de QZ Tray (HTTP ${certificateResponse.status}).`);
        }
        const certificate = await certificateResponse.text();

        qzClient.security.setCertificatePromise((resolve) => resolve(certificate));
        qzClient.security.setSignatureAlgorithm("SHA512");
        qzClient.security.setSignaturePromise(toSign => (resolve, reject) => {
            window.fetch("/api/restaurant/qz/sign", {
                method: "POST",
                cache: "no-store",
                credentials: "same-origin",
                headers: {
                    "Accept": "text/plain",
                    "Content-Type": "application/json",
                    "X-Requested-With": "XMLHttpRequest"
                },
                body: JSON.stringify({ request: toSign })
            }).then(async response => {
                const responseText = await response.text();
                if (!response.ok) {
                    throw new Error(
                        `OrionERP no pudo firmar la solicitud de QZ Tray (HTTP ${response.status}).`);
                }
                resolve(responseText);
            }).catch(reject);
        });
    })().catch(error => {
        restaurantQzSecurityConfiguration = null;
        throw error;
    });

    return restaurantQzSecurityConfiguration;
};

window.restaurantUi = {
    openCashDrawer: async function (printerNameHint) {
        const qzClient = window.qz;
        if (!qzClient) {
            throw new Error("No se pudo cargar el conector local QZ Tray.");
        }

        try {
            await configureRestaurantQzSecurity(qzClient);
        } catch (error) {
            const detail = error && error.message ? ` ${error.message}` : "";
            throw new Error(`No se pudo configurar la firma segura de QZ Tray.${detail}`);
        }

        try {
            if (!qzClient.websocket.isActive()) {
                await qzClient.websocket.connect({ retries: 2, delay: 1 });
            }
        } catch {
            throw new Error("QZ Tray no está instalado o abierto, o el navegador bloqueó el acceso local.");
        }

        const hint = (printerNameHint || "TM-T20").trim();
        let printerNames;
        try {
            const discovered = await qzClient.printers.find();
            printerNames = Array.isArray(discovered) ? discovered : [discovered];
        } catch {
            throw new Error("QZ Tray no pudo consultar las impresoras de esta laptop.");
        }

        let storedPrinter = null;
        try {
            storedPrinter = window.localStorage.getItem("orionerp.restaurant.cashDrawerPrinter");
        } catch {
            // Private browsing or storage policy can disable localStorage; printer discovery still works.
        }
        let printerName = printerNames.find(name =>
            storedPrinter && name.localeCompare(storedPrinter, undefined, { sensitivity: "accent" }) === 0);

        if (!printerName) {
            const matches = printerNames.filter(name =>
                typeof name === "string" && name.toLocaleUpperCase().includes(hint.toLocaleUpperCase()));
            if (matches.length === 0) {
                throw new Error(`No se encontró una impresora cuyo nombre contenga “${hint}”.`);
            }
            if (matches.length > 1) {
                throw new Error(`Hay varias impresoras que contienen “${hint}”; deja conectada una sola TM-T20.`);
            }

            printerName = matches[0];
            try {
                window.localStorage.setItem("orionerp.restaurant.cashDrawerPrinter", printerName);
            } catch {
                // Remembering the queue is an optimization, not a requirement.
            }
        }

        const config = qzClient.configs.create(printerName, {
            jobName: "OrionERP - Abrir cajón"
        });
        const data = [{
            type: "raw",
            format: "command",
            flavor: "hex",
            // ESC p 0 50 250: drawer connector pin 2, 100 ms ON, 500 ms OFF.
            data: "1B700032FA"
        }];

        try {
            await qzClient.print(config, data);
            return printerName;
        } catch (error) {
            const detail = error && error.message ? ` ${error.message}` : "";
            throw new Error(`QZ Tray no pudo enviar el pulso al cajón.${detail}`);
        }
    },

    announceOrder: function (folio, orderType, tableName, customerName) {
        if (!("speechSynthesis" in window)) return;
        const typeText = orderType === "Table" && tableName
            ? `para ${tableName}`
            : orderType === "Delivery"
                ? "para domicilio"
                : "para recoger";
        const customerText = customerName && customerName.trim()
            ? `${customerName.trim()}, su orden ${folio}`
            : `Orden ${folio}`;
        const utterance = new SpeechSynthesisUtterance(`${customerText}, ${typeText}, está lista.`);
        utterance.lang = "es-MX";
        utterance.rate = 0.88;
        window.speechSynthesis.speak(utterance);
    },

    printPdf: function (base64Pdf, fileName) {
        return new Promise((resolve, reject) => {
            try {
                const binary = window.atob(base64Pdf);
                const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
                const pdfUrl = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
                const iframe = document.createElement("iframe");
                let cleanedUp = false;

                const cleanup = () => {
                    if (cleanedUp) return;
                    cleanedUp = true;
                    iframe.remove();
                    URL.revokeObjectURL(pdfUrl);
                };

                iframe.title = fileName || "Tickets de restaurante";
                iframe.style.position = "fixed";
                iframe.style.width = "1px";
                iframe.style.height = "1px";
                iframe.style.right = "0";
                iframe.style.bottom = "0";
                iframe.style.border = "0";
                iframe.style.opacity = "0";

                iframe.addEventListener("load", () => {
                    const printWindow = iframe.contentWindow;
                    if (!printWindow) {
                        cleanup();
                        reject(new Error("El navegador no pudo abrir el PDF para imprimir."));
                        return;
                    }

                    printWindow.addEventListener("afterprint", cleanup, { once: true });
                    window.setTimeout(cleanup, 120000);
                    window.setTimeout(() => {
                        try {
                            printWindow.focus();
                            // Each PDF page is one physical ticket. The Epson driver can cut at End of Page.
                            printWindow.print();
                            resolve();
                        } catch (error) {
                            cleanup();
                            reject(error);
                        }
                    }, 250);
                }, { once: true });

                document.body.appendChild(iframe);
                iframe.src = pdfUrl;
            } catch (error) {
                reject(error);
            }
        });
    }
};
