// Ortaq shell: sidebar, topbar, dark/light keçid. Wireframe-lərdə təkrarı aradan qaldırır.
const NAV = [
  ['index.html', '▦', 'Dashboard'], ['calendar.html', '📅', 'Təqvim'], ['patient.html', '👤', 'Pasiyentlər'],
  ['odontogram.html', '🦷', 'Odontoqram'], ['billing.html', '💳', 'Billing / POS'],
];
const LATER = ['Anbar', 'Laboratoriya', 'Rentgen', 'HR', 'Mühasibat', 'CRM', 'Hesabatlar'];
function mountShell(active, titleHtml) {
  const saved = (() => { try { return localStorage.getItem('theme'); } catch { return null; } })();
  if (saved) document.documentElement.dataset.theme = saved;
  const main = document.getElementById('app');
  const shell = document.createElement('div');
  shell.className = 'shell';
  shell.innerHTML = `
    <aside class="sidebar"><div class="brand">🦷 DentaCore</div>
      <nav class="nav" aria-label="Əsas naviqasiya">
        ${NAV.map(([h, i, t]) => `<a href="${h}" ${t === active ? 'aria-current="page"' : ''}><span aria-hidden="true">${i}</span>${t}</a>`).join('')}
        <div class="group">Növbəti fazalar</div>
        ${LATER.map(t => `<a href="#" style="opacity:.5" aria-disabled="true">${t}</a>`).join('')}
      </nav></aside>
    <header class="topbar">
      <div class="search" role="search">🔍 Pasiyent, telefon, kart № axtar… <kbd>Ctrl K</kbd></div>
      <select aria-label="Filial" class="btn tonal"><option>Mərkəz filialı</option><option>Yasamal</option></select>
      <span class="spacer"></span>
      <button class="icon-btn" aria-label="Bildirişlər">🔔</button>
      <button class="icon-btn" id="themeBtn" aria-label="Tema dəyiş">🌓</button>
      <span class="chip ok">● Online</span>
      <div class="chip">Dr. Nərmin Əliyeva</div>
    </header>`;
  main.replaceWith(shell); shell.appendChild(main);
  document.getElementById('themeBtn').onclick = () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem('theme', next); } catch {}
  };
  const n = document.createElement('div'); n.className = 'wf-note'; n.textContent = 'Wireframe: sabit nümunə data'; document.body.appendChild(n);
}
