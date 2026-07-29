(() => {
  const signage = document.querySelector("[data-menu-signage]");
  if (!signage) {
    return;
  }

  const slides = [...signage.querySelectorAll(".menu-signage__slide")];
  if (slides.length < 2) {
    return;
  }

  const configuredInterval = Number(signage.dataset.intervalMs);
  const intervalMs = Number.isFinite(configuredInterval) && configuredInterval > 0
    ? configuredInterval
    : 8000;
  let activeIndex = 0;

  window.setInterval(() => {
    slides[activeIndex].classList.remove("is-active");
    activeIndex = (activeIndex + 1) % slides.length;
    slides[activeIndex].classList.add("is-active");
  }, intervalMs);

  window.setInterval(() => {
    const version = Date.now();

    slides.forEach((slide, index) => {
      const source = slide.dataset.source;
      if (!source) {
        return;
      }

      const refreshedImage = new Image();
      refreshedImage.onload = () => {
        slides[index].src = refreshedImage.src;
      };
      refreshedImage.src = `${source}?v=${version}`;
    });
  }, 300000);
})();
