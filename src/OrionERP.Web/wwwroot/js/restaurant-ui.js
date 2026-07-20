window.restaurantUi = {
    announceOrder: function (folio, orderType, tableName) {
        if (!("speechSynthesis" in window)) return;
        const typeText = orderType === "Table" && tableName
            ? `para ${tableName}`
            : orderType === "Delivery"
                ? "para domicilio"
                : "para recoger";
        const utterance = new SpeechSynthesisUtterance(`Orden ${folio}, ${typeText}, está lista.`);
        utterance.lang = "es-MX";
        utterance.rate = 0.88;
        window.speechSynthesis.speak(utterance);
    }
};
