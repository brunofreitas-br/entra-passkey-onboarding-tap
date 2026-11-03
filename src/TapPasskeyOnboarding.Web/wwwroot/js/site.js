document.querySelectorAll("[data-copy-target]").forEach((button) => {
  button.addEventListener("click", async () => {
    const target = document.getElementById(button.dataset.copyTarget);
    if (!target) return;
    await navigator.clipboard.writeText(target.textContent.trim());
    button.textContent = "Código copiado";
  });
});

document.querySelectorAll("time[data-local-time]").forEach((element) => {
  const date = new Date(element.dateTime);
  if (Number.isNaN(date.getTime())) return;

  const options = {
    dateStyle: "short",
    timeStyle: "short"
  };
  element.textContent = new Intl.DateTimeFormat(undefined, options).format(date);

  const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const label = element.parentElement?.querySelector("[data-time-zone-label]");
  if (label) {
    label.textContent = timeZone ? `(horário local · ${timeZone})` : "(horário local)";
  }
});
