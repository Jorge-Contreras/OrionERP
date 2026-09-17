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

  // Clip pide cargar el SDK siempre desde su dominio, sin empaquetarlo ni
  // auto-hospedarlo, para que la tokenizacion siga sus cambios.
  const clipSdkUrl = 'https://sdk.clip.mx/js/clip-sdk.js';

  const loadClipSdk = (apiKey) => {
    const key = `clip|${apiKey}`;
    if (sdkPromises.has(key)) return sdkPromises.get(key);
    const promise = new Promise((resolve, reject) => {
      if (window.ClipSDK) {
        resolve(new window.ClipSDK(apiKey));
        return;
      }
      const existing = document.querySelector(`script[src="${clipSdkUrl}"]`);
      const onReady = () => window.ClipSDK
        ? resolve(new window.ClipSDK(apiKey))
        : reject(new Error('El formulario de pago no cargó. Actualiza la página e inténtalo de nuevo.'));
      if (existing) {
        existing.addEventListener('load', onReady, { once: true });
        existing.addEventListener('error', () => reject(new Error('No se pudo cargar el formulario de pago.')), { once: true });
        return;
      }
      const script = document.createElement('script');
      script.src = clipSdkUrl;
      script.async = true;
      script.onload = onReady;
      script.onerror = () => reject(new Error('No se pudo cargar el formulario de pago. Revisa tu conexión e inténtalo de nuevo.'));
      document.head.appendChild(script);
    });
    sdkPromises.set(key, promise);
    return promise;
  };

  // Estado del formulario montado. Un solo elemento de tarjeta a la vez: el SDK
  // inserta un iframe propio y montar dos dejaria tokens huerfanos.
  let cardElement = null;
  let cardContainerId = null;

  const threeDsContainerId = 'bruno-3ds-frame';

  const removeThreeDsFrame = () => {
    document.getElementById(threeDsContainerId)?.remove();
  };

  /*
   * Abre la autenticacion 3DS del banco en un iframe y espera a que el emisor
   * avise el resultado. Cerrar esta ventana NO cancela el pago: el cargo sigue
   * vivo en Clip, asi que quien cierre se va al seguimiento, no a pagar de nuevo.
   */
  const runThreeDsAsync = (url) => new Promise((resolve) => {
    let expectedOrigin;
    try {
      expectedOrigin = new URL(url).origin;
    } catch {
      resolve({ outcome: 'invalid' });
      return;
    }

    removeThreeDsFrame();
    const overlay = document.createElement('div');
    overlay.id = threeDsContainerId;
    overlay.className = 'checkout-3ds';
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-modal', 'true');
    overlay.setAttribute('aria-label', 'Verificación de tu banco');

    const frame = document.createElement('iframe');
    frame.title = 'Verificación de tu banco';
    frame.src = url;
    frame.className = 'checkout-3ds__frame';

    const dismiss = document.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'checkout-3ds__dismiss';
    dismiss.textContent = 'Cerrar';

    overlay.append(frame, dismiss);
    document.body.appendChild(overlay);

    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      window.removeEventListener('message', onMessage);
      clearTimeout(timer);
      removeThreeDsFrame();
      resolve(result);
    };

    const onMessage = (event) => {
      if (event.origin !== expectedOrigin) return;
      const paymentId = event.data?.paymentId;
      if (typeof paymentId === 'string' && paymentId.length > 0 && paymentId.length <= 64) {
        finish({ outcome: 'completed', paymentId });
      }
    };

    window.addEventListener('message', onMessage);
    dismiss.addEventListener('click', () => finish({ outcome: 'dismissed' }));
    // El emisor puede dejar la ventana abierta indefinidamente. Pasado el
    // limite se deja de esperar, pero el pago sigue su curso del lado de Clip.
    const timer = setTimeout(() => finish({ outcome: 'timeout' }), 10 * 60 * 1000);
  });

  const rememberPendingCheckout = (clientAttemptId, trackingToken) => {
    const pending = {
      clientAttemptId,
      trackingToken,
      savedAtUtc: new Date().toISOString()
    };
    try {
      sessionStorage.setItem(checkoutKey(clientAttemptId), JSON.stringify(pending));
      sessionStorage.setItem(currentCheckoutKey, JSON.stringify(pending));
    } catch { /* Una sesion privada sin almacenamiento no debe frenar el pago. */ }
  };

  // Toda falla del pago viaja como dato, nunca como excepcion: si el JS lanza,
  // Blazor Server reemplaza el texto y el cliente termina viendo un mensaje
  // generico en lugar de la razon real.
  const failure = (code, message, trackingToken = '') => ({
    succeeded: false,
    code,
    message,
    trackingToken,
    checkoutStatus: '',
    paymentCaptured: false,
    isPending: false
  });

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

    /**
     * Monta el formulario de tarjeta de Clip. Los campos viven dentro de un
     * iframe servido por Clip, así que este sitio nunca toca los datos de la
     * tarjeta.
     */
    async mountCardForm(containerId, options) {
      const container = document.getElementById(containerId);
      if (!container || !options?.apiKey) return false;
      if (cardElement && cardContainerId === containerId) return true;

      try {
        const clip = await loadClipSdk(options.apiKey);
        window.brunoOrdering.clearCardForm(containerId);
        cardElement = clip.element.create('Card', {
          theme: options.theme === 'dark' ? 'dark' : 'light',
          locale: options.locale === 'en' ? 'en' : 'es'
        });
        cardElement.mount(containerId);
        cardContainerId = containerId;
        return true;
      } catch {
        cardElement = null;
        cardContainerId = null;
        return false;
      }
    },

    clearCardForm(containerId) {
      removeThreeDsFrame();
      cardElement = null;
      cardContainerId = null;
      const container = document.getElementById(containerId);
      if (container) container.replaceChildren();
    },

    /**
     * Tokeniza la tarjeta y cobra. El token es de un solo uso, así que un
     * reintento siempre parte de una tokenización nueva; el servidor es quien
     * garantiza que sólo un cargo pueda estar en vuelo por pedido.
     */
    async payWithCard(options) {
      if (!cardElement) return failure('card_form_missing', 'El formulario de pago no está listo. Actualiza la página.');
      if (!options?.quoteToken || !options?.clientAttemptId) {
        return failure('invalid_request', 'No pudimos identificar tu pedido. Vuelve a revisar el carrito.');
      }

      // El intento se reserva antes de tokenizar: si la cotización cambió, el
      // cliente se entera sin haber entregado su tarjeta.
      let intent;
      try {
        intent = await postJson('/api/restaurant/checkout/intents', {
          quoteToken: options.quoteToken,
          clientAttemptId: options.clientAttemptId,
          customerName: options.customerName,
          customerEmail: options.customerEmail,
          customerPhone: options.customerPhone,
          termsAccepted: options.termsAccepted,
          termsVersion: options.termsVersion,
          privacyVersion: options.privacyVersion
        });
      } catch (error) {
        return failure(error.code || 'checkout_failed', error.message || 'No pudimos preparar tu pago.');
      }
      if (!intent?.trackingToken) {
        return failure('checkout_failed', intent?.message || 'No pudimos preparar tu pago.');
      }
      rememberPendingCheckout(options.clientAttemptId, intent.trackingToken);

      let cardTokenId;
      try {
        const cardToken = await cardElement.cardToken();
        cardTokenId = cardToken?.id;
      } catch (error) {
        return failure(
          error?.code || 'card_token_failed',
          error?.message || 'Revisa los datos de tu tarjeta e inténtalo de nuevo.',
          intent.trackingToken);
      }
      if (!cardTokenId) {
        return failure('card_token_failed', 'Revisa los datos de tu tarjeta e inténtalo de nuevo.', intent.trackingToken);
      }

      let charge;
      try {
        charge = await postJson('/api/restaurant/checkout/charge', {
          quoteToken: options.quoteToken,
          clientAttemptId: options.clientAttemptId,
          trackingToken: intent.trackingToken,
          cardTokenId
        });
      } catch (error) {
        // Una falla aquí no significa que no se cobró. El seguimiento manda.
        return failure(
          error.code || 'charge_failed',
          error.message || 'No pudimos confirmar el pago. Revisa el estado de tu pedido antes de intentar otra vez.',
          intent.trackingToken);
      }

      if (!charge?.threeDSecureUrl) return charge;

      const verification = await runThreeDsAsync(charge.threeDSecureUrl);
      if (verification.outcome !== 'completed') {
        // Sin resultado del banco no se vuelve a cobrar: el pago sigue vivo y
        // la página de seguimiento dirá en qué terminó.
        return {
          ...charge,
          message: 'Seguimos confirmando el pago con tu banco. No vuelvas a pagar.',
          isPending: true
        };
      }

      try {
        return await postJson('/api/restaurant/checkout/charge/confirm', {
          clientAttemptId: options.clientAttemptId,
          trackingToken: intent.trackingToken,
          paymentId: verification.paymentId
        });
      } catch (error) {
        return failure(
          error.code || 'charge_confirm_failed',
          error.message || 'No pudimos confirmar el resultado de la verificación. Revisa el estado de tu pedido.',
          intent.trackingToken);
      }
    }
  };
})();
