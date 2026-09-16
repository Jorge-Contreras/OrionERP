(() => {
  'use strict';

  const cartVersion = 1;
  const sdkPromises = new Map();

  const storageKey = (publicSiteKey) => `orion.restaurant.cart.v${cartVersion}.${publicSiteKey || 'site'}`;
  const checkoutKey = (attemptId) => `orion.restaurant.checkout.v1.${attemptId || 'current'}`;
  const currentCheckoutKey = 'orion.restaurant.checkout.v1.current';
  const csrfToken = () => document.querySelector('meta[name="csrf-token"]')?.getAttribute('content') || '';

  const clearPendingCheckoutAttempt = (attemptId) => {
    sessionStorage.removeItem(checkoutKey(attemptId));
    try {
      const current = JSON.parse(sessionStorage.getItem(currentCheckoutKey) || 'null');
      if (!current?.clientAttemptId || String(current.clientAttemptId) === String(attemptId)) {
        sessionStorage.removeItem(currentCheckoutKey);
      }
    } catch {
      sessionStorage.removeItem(currentCheckoutKey);
    }
  };

  const cleanString = (value, maximum = 200) => typeof value === 'string'
    ? value.trim().slice(0, maximum)
    : '';

  const cleanIdList = (values) => Array.isArray(values)
    ? [...new Set(values.map(Number).filter(value => Number.isSafeInteger(value) && value > 0))].slice(0, 50)
    : [];

  const cleanCart = (value) => {
    if (!Array.isArray(value)) return [];

    return value.slice(0, 50).flatMap((line) => {
      const productId = Number(line?.productId);
      const menuSectionId = Number(line?.menuSectionId);
      if (!Number.isSafeInteger(productId) || productId <= 0 ||
          !Number.isSafeInteger(menuSectionId) || menuSectionId <= 0) return [];

      const comboSelections = Array.isArray(line.comboSelections)
        ? line.comboSelections.slice(0, 30).flatMap((selection) => {
          const comboSlotId = Number(selection?.comboSlotId);
          const comboSlotOptionId = Number(selection?.comboSlotOptionId);
          if (!Number.isSafeInteger(comboSlotId) || comboSlotId <= 0 ||
              !Number.isSafeInteger(comboSlotOptionId) || comboSlotOptionId <= 0) return [];
          return [{
            comboSlotId,
            comboSlotOptionId,
            slotName: cleanString(selection.slotName),
            optionName: cleanString(selection.optionName),
            priceDelta: Number(selection.priceDelta) || 0,
            modifierOptionIds: cleanIdList(selection.modifierOptionIds),
            modifierNames: Array.isArray(selection.modifierNames)
              ? selection.modifierNames.map(item => cleanString(item)).filter(Boolean).slice(0, 50)
              : [],
            notes: cleanString(selection.notes, 500) || null
          }];
        })
        : [];

      return [{
        key: cleanString(line.key, 80) || createAttemptId(),
        productId,
        menuSectionId,
        productName: cleanString(line.productName),
        quantity: Math.min(20, Math.max(1, Number(line.quantity) || 1)),
        displayUnitPrice: Math.max(0, Number(line.displayUnitPrice) || 0),
        modifierOptionIds: cleanIdList(line.modifierOptionIds),
        modifierNames: Array.isArray(line.modifierNames)
          ? line.modifierNames.map(item => cleanString(item)).filter(Boolean).slice(0, 50)
          : [],
        comboSelections,
        notes: cleanString(line.notes, 500) || null
      }];
    });
  };

  const createAttemptId = () => window.crypto && typeof window.crypto.randomUUID === 'function'
    ? window.crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;

  const notifyCartChanged = (publicSiteKey, cart) => {
    const count = cart.reduce((total, line) => total + (Number(line.quantity) || 0), 0);
    window.dispatchEvent(new CustomEvent('bruno:cart-changed', {
      detail: { publicSiteKey, count }
    }));
  };

  const readBody = async (response) => {
    try { return await response.json(); }
    catch { return null; }
  };

  const problemMessage = (body, fallback) => body?.detail || body?.message || body?.title || fallback;

  const postJson = async (url, body) => {
    const response = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Accept': 'application/json',
        'Content-Type': 'application/json',
        'X-CSRF-TOKEN': csrfToken()
      },
      body: JSON.stringify(body)
    });
    const responseBody = await readBody(response);
    if (!response.ok) {
      const error = new Error(problemMessage(responseBody, 'No se pudo completar la solicitud.'));
      error.code = responseBody?.errorCode || responseBody?.extensions?.errorCode || '';
      error.status = response.status;
      throw error;
    }
    return responseBody;
  };

  const loadPayPalSdk = (clientId, currency, locale) => {
    const key = `${clientId}|${currency}|${locale}`;
    if (sdkPromises.has(key)) return sdkPromises.get(key);

    const promise = new Promise((resolve, reject) => {
      if (window.paypal?.Buttons) {
        resolve(window.paypal);
        return;
      }

      const script = document.createElement('script');
      const parameters = new URLSearchParams({
        'client-id': clientId,
        currency: currency || 'MXN',
        intent: 'capture',
        locale: locale || 'es_MX',
        components: 'buttons'
      });
      script.src = `https://www.paypal.com/sdk/js?${parameters}`;
      script.async = true;
      script.onload = () => resolve(window.paypal);
      script.onerror = () => reject(new Error('No se pudo cargar PayPal. Revisa tu conexión e inténtalo de nuevo.'));
      document.head.appendChild(script);
    });
    sdkPromises.set(key, promise);
    return promise;
  };

  const notify = async (dotNetReference, method, ...args) => {
    try { await dotNetReference.invokeMethodAsync(method, ...args); }
    catch { /* The PayPal popup can outlive a reconnecting Blazor circuit. */ }
  };

  window.brunoOrdering = {
    createAttemptId,

    loadCart(publicSiteKey) {
      try {
        const parsed = JSON.parse(localStorage.getItem(storageKey(publicSiteKey)) || '[]');
        return cleanCart(parsed);
      } catch {
        return [];
      }
    },

    saveCart(publicSiteKey, cart) {
      const clean = cleanCart(cart);
      localStorage.setItem(storageKey(publicSiteKey), JSON.stringify(clean));
      notifyCartChanged(publicSiteKey, clean);
      return clean;
    },

    clearCart(publicSiteKey) {
      localStorage.removeItem(storageKey(publicSiteKey));
      notifyCartChanged(publicSiteKey, []);
    },

    loadPendingCheckout() {
      try {
        const value = JSON.parse(sessionStorage.getItem(currentCheckoutKey) || 'null');
        if (!value?.trackingToken || !value?.clientAttemptId) return null;
        const savedAt = Date.parse(value.savedAtUtc || '');
        if (!Number.isFinite(savedAt) || Date.now() - savedAt > 2 * 60 * 60 * 1000) {
          sessionStorage.removeItem(currentCheckoutKey);
          return null;
        }
        return value;
      } catch {
        sessionStorage.removeItem(currentCheckoutKey);
        return null;
      }
    },

    clearPendingCheckout() {
      sessionStorage.removeItem(currentCheckoutKey);
    },

    async quoteCart(request) {
      try {
        return await postJson('/api/restaurant/checkout/quote', request);
      } catch (error) {
        // Blazor Server replaces the text of a JS interop exception unless
        // detailed errors are on, so the server's reason only reaches the page
        // as data. Return the failure instead of throwing it.
        return {
          succeeded: false,
          code: error.code || 'quote_failed',
          message: error.message || 'No pudimos confirmar el precio de tu pedido.'
        };
      }
    },

    async getStatus(trackingToken) {
      const response = await fetch(`/api/restaurant/checkout/status/${encodeURIComponent(trackingToken)}`, {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
        cache: 'no-store'
      });
      const body = await readBody(response);
      if (!response.ok) throw new Error(problemMessage(body, 'No encontramos este pedido.'));
      return body;
    },

    async renderPayPalButtons(containerId, options, dotNetReference) {
      const container = document.getElementById(containerId);
      if (!container || !options?.clientId || !options?.quoteToken || !options?.clientAttemptId) return false;

      const renderKey = [
        options.clientId,
        options.currency,
        options.locale,
        options.quoteFingerprint,
        options.clientAttemptId,
        options.termsAccepted === true ? 'accepted' : 'pending'
      ].join('|');
      if (container.dataset.paypalRenderKey === renderKey && container.childElementCount > 0) return true;

      container.replaceChildren();
      try {
        const paypal = await loadPayPalSdk(options.clientId, options.currency || 'MXN', options.locale || 'es_MX');
        await paypal.Buttons({
          style: { layout: 'vertical', color: 'gold', shape: 'rect', label: 'paypal', tagline: false },
          async createOrder() {
            await notify(dotNetReference, 'OnPaymentStarted');
            const result = await postJson('/api/restaurant/checkout/paypal-orders', {
              quoteToken: options.quoteToken,
              clientAttemptId: options.clientAttemptId,
              customerName: options.customerName,
              customerEmail: options.customerEmail,
              customerPhone: options.customerPhone,
              termsAccepted: options.termsAccepted === true,
              termsVersion: options.termsVersion,
              privacyVersion: options.privacyVersion
            });
            if (!result?.payPalOrderId || !result?.trackingToken) {
              throw new Error(result?.message || 'PayPal no devolvió una orden válida.');
            }
            const pendingCheckout = {
              clientAttemptId: options.clientAttemptId,
              orderId: result.payPalOrderId,
              trackingToken: result.trackingToken,
              savedAtUtc: new Date().toISOString()
            };
            sessionStorage.setItem(checkoutKey(options.clientAttemptId), JSON.stringify(pendingCheckout));
            sessionStorage.setItem(currentCheckoutKey, JSON.stringify(pendingCheckout));
            await notify(dotNetReference, 'OnPayPalOrderCreated', result.payPalOrderId, result.trackingToken);
            return result.payPalOrderId;
          },
          async onApprove(data) {
            const saved = JSON.parse(sessionStorage.getItem(checkoutKey(options.clientAttemptId)) || '{}');
            const orderId = data?.orderID || saved.orderId;
            if (!orderId || !saved.trackingToken) throw new Error('No se pudo recuperar la referencia segura del pedido.');
            const capture = await postJson(`/api/restaurant/checkout/paypal-orders/${encodeURIComponent(orderId)}/capture`, {
              quoteToken: options.quoteToken,
              clientAttemptId: options.clientAttemptId,
              trackingToken: saved.trackingToken
            });
            await notify(dotNetReference, 'OnPaymentCompleted', JSON.stringify(capture));
          },
          async onCancel() {
            clearPendingCheckoutAttempt(options.clientAttemptId);
            await notify(dotNetReference, 'OnPaymentCancelled', options.clientAttemptId);
          },
          async onError(error) {
            const saved = JSON.parse(sessionStorage.getItem(checkoutKey(options.clientAttemptId)) || '{}');
            await notify(
              dotNetReference,
              'OnPaymentFailed',
              error?.message || 'No se pudo completar el pago.',
              saved.orderId || '',
              saved.trackingToken || '');
          }
        }).render(container);
        container.dataset.paypalRenderKey = renderKey;
        return true;
      } catch (error) {
        await notify(dotNetReference, 'OnPaymentFailed', error?.message || 'No se pudo iniciar PayPal.', '', '');
        return false;
      }
    },

    clearPayPalButtons(containerId) {
      const container = document.getElementById(containerId);
      if (container) {
        delete container.dataset.paypalRenderKey;
        container.replaceChildren();
      }
    }
  };
})();
