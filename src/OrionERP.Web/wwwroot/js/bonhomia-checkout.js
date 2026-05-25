(function () {
  const sdkPromises = new Map();

  const loadPayPalSdk = (clientId, currency) => {
    const key = `${clientId}|${currency}`;
    if (sdkPromises.has(key)) {
      return sdkPromises.get(key);
    }

    const promise = new Promise((resolve, reject) => {
      if (window.paypal && window.paypal.Buttons) {
        resolve(window.paypal);
        return;
      }

      const script = document.createElement("script");
      const params = new URLSearchParams({
        "client-id": clientId,
        currency: currency || "MXN",
        intent: "capture"
      });

      script.src = `https://www.paypal.com/sdk/js?${params.toString()}`;
      script.async = true;
      script.onload = () => resolve(window.paypal);
      script.onerror = () => reject(new Error("No se pudo cargar PayPal."));
      document.head.appendChild(script);
    });

    sdkPromises.set(key, promise);
    return promise;
  };

  const parseProblem = async (response) => {
    try {
      const body = await response.json();
      return body.detail || body.title || "No se pudo completar el pago.";
    } catch {
      return "No se pudo completar el pago.";
    }
  };

  const notify = async (dotNetRef, methodName, ...args) => {
    try {
      await dotNetRef.invokeMethodAsync(methodName, ...args);
    } catch {
      // The PayPal popup can outlive a transient Blazor circuit reconnect.
    }
  };

  const isContainerRemovedError = (error) => {
    const message = error && error.message ? error.message : String(error || "");
    return message.includes("Detected container element removed from DOM");
  };

  const buildRenderKey = (options) => options && options.renderKey
    ? options.renderKey
    : [
      options?.clientId || "",
      options?.currency || "MXN",
      options?.quoteToken || "",
      options?.quoteFingerprint || "",
      options?.customer?.fullName || options?.customer?.FullName || "",
      options?.customer?.email || options?.customer?.Email || "",
      options?.customer?.phone || options?.customer?.Phone || ""
    ].join("|");

  const createAttemptId = () => {
    if (window.crypto && typeof window.crypto.randomUUID === "function") {
      return window.crypto.randomUUID();
    }

    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
  };

  const submitPayPalConfirmation = async (orderId, options, dotNetRef, paymentAttemptId) => {
    if (!orderId) {
      await notify(dotNetRef, "OnBonhomiaPaymentConfirmationFailed", "La orden PayPal es obligatoria.", "", paymentAttemptId || "");
      return;
    }

    try {
      await notify(dotNetRef, "OnBonhomiaPaymentProcessing");
      const response = await fetch(`/api/bonhomia/checkout/orders/${encodeURIComponent(orderId)}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Accept": "application/json"
        },
        body: JSON.stringify({
          quoteToken: options.quoteToken,
          quoteFingerprint: options.quoteFingerprint,
          paymentAttemptId,
          customer: options.customer
        })
      });

      if (!response.ok) {
        throw new Error(await parseProblem(response));
      }

      const result = await response.json();
      await notify(dotNetRef, "OnBonhomiaPaymentCompleted", result);
    } catch (error) {
      const message = error && error.message ? error.message : "No se pudo completar el pago.";
      await notify(dotNetRef, "OnBonhomiaPaymentConfirmationFailed", message, orderId, paymentAttemptId || "");
    }
  };

  window.bonhomiaCheckout = {
    async renderPayPalButtons(containerId, options, dotNetRef) {
      const container = document.getElementById(containerId);
      if (!container) {
        return false;
      }

      if (!options || !options.clientId || !options.quoteToken) {
        return false;
      }

      const renderKey = buildRenderKey(options);
      if (container.dataset.paypalRenderKey === renderKey && container.childElementCount > 0) {
        return true;
      }

      if (container.dataset.paypalRenderInFlightKey === renderKey) {
        return true;
      }

      container.dataset.paypalRenderInFlightKey = renderKey;
      container.replaceChildren();
      const paymentAttemptId = createAttemptId();

      try {
        const paypal = await loadPayPalSdk(options.clientId, options.currency || "MXN");
        if (!paypal || !paypal.Buttons) {
          throw new Error("PayPal no esta disponible.");
        }

        await paypal.Buttons({
          style: {
            layout: "vertical",
            color: "gold",
            shape: "rect",
            label: "paypal",
            tagline: false
          },
          async createOrder() {
            const response = await fetch("/api/bonhomia/checkout/orders", {
              method: "POST",
              headers: {
                "Content-Type": "application/json",
                "Accept": "application/json"
              },
              body: JSON.stringify({
                quoteToken: options.quoteToken,
                quoteFingerprint: options.quoteFingerprint,
                paymentAttemptId
              })
            });

            if (!response.ok) {
              throw new Error(await parseProblem(response));
            }

            const order = await response.json();
            return order.id;
          },
          async onApprove(data) {
            await submitPayPalConfirmation(data.orderID, options, dotNetRef, paymentAttemptId);
          },
          async onCancel() {
            await notify(dotNetRef, "OnBonhomiaPaymentCancelled");
          },
          async onError(error) {
            if (isContainerRemovedError(error)) {
              return;
            }

            const message = error && error.message ? error.message : "No se pudo completar el pago.";
            await notify(dotNetRef, "OnBonhomiaPaymentFailed", message);
          }
        }).render(container);

        container.dataset.paypalRenderKey = renderKey;
        return true;
      } catch (error) {
        if (isContainerRemovedError(error)) {
          return false;
        }

        const message = error && error.message ? error.message : "No se pudo inicializar PayPal.";
        await notify(dotNetRef, "OnBonhomiaPaymentFailed", message);
        return false;
      } finally {
        if (container.dataset.paypalRenderInFlightKey === renderKey) {
          delete container.dataset.paypalRenderInFlightKey;
        }
      }
    },

    async confirmPayPalOrder(orderId, options, dotNetRef) {
      await submitPayPalConfirmation(orderId, options, dotNetRef, options?.paymentAttemptId || "");
    },

    clearPayPalButtons(containerId) {
      const container = document.getElementById(containerId);
      if (container) {
        delete container.dataset.paypalRenderKey;
        delete container.dataset.paypalRenderInFlightKey;
        container.replaceChildren();
      }
    },

    scrollIntoView(elementId) {
      const element = document.getElementById(elementId);
      if (element) {
        element.scrollIntoView({ behavior: "smooth", block: "start" });
      }
    },

    focusElement(elementId) {
      const element = document.getElementById(elementId);
      if (element) {
        element.scrollIntoView({ behavior: "smooth", block: "center" });
        element.focus({ preventScroll: true });
      }
    }
  };
})();
