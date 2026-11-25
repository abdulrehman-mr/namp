<?php /* NAMP-WELCOME - default page of NAMP, the free local server by Lvato (https://lvato.com) */
$site  = strtolower(preg_replace('/:\d+$/', '', $_SERVER['HTTP_HOST'] ?? 'localhost'));
$lvato = 'https://lvato.com/?utm_source=namp&amp;utm_medium=welcome_page&amp;utm_campaign=local_server';
?>
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title><?= htmlspecialchars($site) ?> · NAMP by Lvato</title>
<style>
  :root {
    --page: #FDFDFC; --card: #FFFFFF; --ink: #1B1B18; --muted: #706F6C; --ring: rgba(26, 26, 0, .16);
    --dot: #E3E3E0; --blue: #0A74E6; --art: #EEF6FF; --btn: #1B1B18; --btn-ink: #FFFFFF;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --page: #0A0A0A; --card: #161615; --ink: #EDEDEC; --muted: #A1A09A; --ring: rgba(255, 250, 237, .18);
      --dot: #3E3E3A; --blue: #4BA3FF; --art: #0B1A2E; --btn: #EEEEEC; --btn-ink: #1C1C1A;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 24px;
    background: var(--page); color: var(--ink);
    font: 13px/20px "Segoe UI", system-ui, -apple-system, "Helvetica Neue", sans-serif; -webkit-font-smoothing: antialiased;
  }
  main { display: flex; width: 100%; max-width: 896px; }
  .left {
    flex: 1; padding: 80px; background: var(--card); box-shadow: inset 0 0 0 1px var(--ring);
    border-radius: 8px 0 0 8px; display: flex; flex-direction: column; justify-content: center;
  }
  h1 { font-size: 13px; line-height: 20px; font-weight: 600; margin: 0 0 4px; }
  p { margin: 0; color: var(--muted); }
  ul { list-style: none; margin: 8px 0 24px; padding: 0; }
  li { position: relative; display: flex; align-items: center; gap: 16px; padding: 8px 0; }
  li:not(:last-child)::before { content: ""; position: absolute; left: 6px; top: 50%; bottom: -50%; border-left: 1px solid var(--dot); }
  li:not(:first-child)::before { content: ""; position: absolute; left: 6px; top: -50%; bottom: 50%; border-left: 1px solid var(--dot); }
  li:not(:first-child):not(:last-child)::before { top: -50%; bottom: -50%; }
  .dot {
    position: relative; z-index: 1; flex: none; width: 14px; height: 14px; border-radius: 50%; background: var(--card);
    border: 1px solid var(--dot); box-shadow: 0 1px 2px rgba(0, 0, 0, .06);
  }
  .dot::after { content: ""; position: absolute; inset: 3px; border-radius: 50%; background: var(--dot); }
  a.link { color: var(--blue); font-weight: 500; text-decoration: underline; text-underline-offset: 4px; display: inline-flex; align-items: center; gap: 4px; margin-left: 4px; }
  a.link svg { width: 10px; height: 10px; }
  .btn {
    display: inline-block; align-self: flex-start; padding: 6px 20px; border-radius: 4px; font-size: 14px; line-height: 20px;
    background: var(--btn); color: var(--btn-ink); border: 1px solid var(--btn); text-decoration: none;
  }
  .btn:hover { opacity: .88; }
  .meta { margin-top: 28px; font-size: 12px; color: var(--muted); }
  .meta code { font: 11.5px Consolas, "Cascadia Code", monospace; }

  .art {
    position: relative; width: 438px; aspect-ratio: 335 / 376; flex: none; overflow: hidden; background: var(--art);
    border-radius: 0 8px 8px 0; box-shadow: inset 0 0 0 1px var(--ring);
  }
  .art .word { position: absolute; top: 34px; left: 32px; width: calc(100% - 64px); }
  .art .gem { position: absolute; width: 86%; left: 7%; bottom: -24%; filter: drop-shadow(0 24px 36px rgba(0, 61, 130, .22)); }
  .art .gem.ghost { width: 46%; left: -10%; bottom: 6%; opacity: .12; filter: none; transform: rotate(-14deg); }

  @media (max-width: 860px) {
    main { flex-direction: column-reverse; max-width: 380px; }
    .left { padding: 24px 24px 40px; border-radius: 0 0 8px 8px; }
    .art { width: 100%; aspect-ratio: 335 / 300; border-radius: 8px 8px 0 0; }
  }
</style>
</head>
<body>
<main>
  <section class="left">
    <h1>Let's get started</h1>
    <p><?= htmlspecialchars($site) ?> is running on NAMP.<br>We suggest starting with the following.</p>
    <ul>
      <li><span class="dot"></span><span>Go live from $0.50/mo with<a class="link" href="<?= $lvato ?>" target="_blank" rel="noopener">Lvato Hosting<svg viewBox="0 0 10 11" fill="none"><path d="M7.7 3.8 3 8.4m4.7-4.6H4.1m3.6 0v3.6" stroke="currentColor" stroke-linecap="square"/></svg></a></span></li>
      <li><span class="dot"></span><span>Manage your databases in<a class="link" href="/phpmyadmin/" target="_blank">phpMyAdmin<svg viewBox="0 0 10 11" fill="none"><path d="M7.7 3.8 3 8.4m4.7-4.6H4.1m3.6 0v3.6" stroke="currentColor" stroke-linecap="square"/></svg></a></span></li>
    </ul>
    <a class="btn" href="<?= $lvato ?>" target="_blank" rel="noopener">Deploy now</a>
  </section>

  <div class="art" aria-hidden="true">
    <svg class="word" viewBox="0.6 3 35.6 11">
      <defs><linearGradient id="lvw" gradientTransform="rotate(25)"><stop offset="0" stop-color="#0091FF"/><stop offset="1" stop-color="#003D82"/></linearGradient></defs>
      <path fill="url(#lvw)" d="M7.08 13.33L1.57 13.33Q1.09 13.33 1.09 12.82L1.09 4.04Q1.09 3.53 1.57 3.53L2.68 3.53Q3.09 3.53 3.09 4.04L3.09 11.63L7.08 11.63Q7.59 11.63 7.59 12.08L7.59 12.89Q7.59 13.33 7.08 13.33ZM10.21 12.87L8.23 6.69Q8.06 6.15 8.46 6.15L9.68 6.15Q10.00 6.15 10.09 6.50L11.15 10.51Q11.24 10.84 11.30 11.17Q11.35 11.50 11.37 11.70L11.41 11.70Q11.42 11.50 11.48 11.17Q11.54 10.84 11.63 10.51L12.69 6.50Q12.78 6.15 13.10 6.15L14.32 6.15Q14.72 6.15 14.55 6.69L12.56 12.87Q12.42 13.33 12.07 13.33L10.71 13.33Q10.36 13.33 10.21 12.87ZM15.38 11.42Q15.38 10.05 16.69 9.43Q17.99 8.82 19.67 8.78L19.67 8.50Q19.67 7.88 19.35 7.62Q19.04 7.36 18.48 7.36Q18.08 7.36 17.78 7.47Q17.47 7.58 17.25 7.73Q17.03 7.88 16.85 8.03Q16.54 8.30 16.33 8.05L15.76 7.36Q15.55 7.10 15.87 6.82Q16.11 6.61 16.42 6.45Q16.74 6.30 17.27 6.16Q17.79 6.02 18.63 6.02Q20.06 6.02 20.82 6.67Q21.58 7.33 21.58 8.61L21.58 12.92Q21.58 13.33 21.17 13.33L20.05 13.33Q19.68 13.33 19.68 12.92L19.68 12.71L19.67 12.71Q19.26 13.10 18.70 13.29Q18.14 13.47 17.66 13.47Q16.54 13.47 15.96 12.92Q15.38 12.37 15.38 11.42ZM17.40 11.38Q17.40 11.61 17.50 11.79Q17.60 11.96 17.80 12.04Q18.01 12.13 18.26 12.13Q18.61 12.13 18.94 12.03Q19.26 11.94 19.67 11.58L19.67 10.10Q18.52 10.23 18.13 10.43Q17.74 10.64 17.57 10.86Q17.40 11.07 17.40 11.38ZM23.67 7.52L23.20 7.52Q22.80 7.52 22.80 7.18L22.80 6.49Q22.80 6.15 23.20 6.15L23.67 6.15L23.67 5.91Q23.67 5.61 23.81 5.35L24.34 4.43Q24.49 4.17 24.69 4.17L25.17 4.17Q25.58 4.17 25.58 4.58L25.58 6.15L26.69 6.15Q27.09 6.15 27.09 6.49L27.09 7.18Q27.09 7.52 26.69 7.52L25.58 7.52L25.58 10.91Q25.58 11.61 25.68 11.91Q25.78 12.22 26.20 12.22Q26.41 12.22 26.67 12.14Q26.94 12.07 27.09 12.00Q27.33 11.89 27.41 12.16L27.61 12.94Q27.67 13.17 27.43 13.28Q27.21 13.38 26.81 13.47Q26.42 13.56 26.02 13.56Q25.05 13.56 24.36 13.15Q23.67 12.75 23.67 11.49L23.67 7.52ZM28.51 9.74Q28.51 8.00 29.41 7.01Q30.31 6.02 32.09 6.02Q33.87 6.02 34.76 7.01Q35.66 8.00 35.66 9.74Q35.66 11.48 34.76 12.48Q33.87 13.47 32.09 13.47Q30.31 13.47 29.41 12.48Q28.51 11.48 28.51 9.74ZM30.54 9.74Q30.54 11.04 30.94 11.58Q31.34 12.13 32.09 12.13Q32.84 12.13 33.24 11.58Q33.64 11.04 33.64 9.74Q33.64 8.44 33.24 7.90Q32.84 7.36 32.09 7.36Q31.34 7.36 30.94 7.90Q30.54 8.44 30.54 9.74Z"/>
    </svg>
    <svg class="gem ghost" viewBox="0 0 64 66"><use href="#lvgem"/></svg>
    <svg class="gem" viewBox="0 0 64 66">
      <defs><linearGradient id="lvg" gradientTransform="rotate(25)"><stop offset="0" stop-color="#003D82"/><stop offset="1" stop-color="#0091FF"/></linearGradient></defs>
      <path id="lvgem" fill="url(#lvg)" d="M56.88 7.526c-.024-.045-.059-.082-.089-.122-.023-.031-.042-.066-.068-.093-.035-.036-.076-.063-.116-.094-.031-.024-.059-.054-.093-.074-.047-.028-.099-.045-.15-.065-.032-.013-.062-.032-.095-.041A1.004 1.004 0 0 0 56 7H8c-.092 0-.183.013-.269.037-.035.01-.066.029-.099.043-.05.02-.101.036-.147.064-.034.02-.063.05-.094.074-.039.03-.08.056-.114.092-.027.029-.046.064-.07.096-.03.04-.064.076-.088.12l-7 13a.998.998 0 0 0 .076 1.068l31 42 .006.005a.974.974 0 0 0 .398.305.98.98 0 0 0 .4.096c.056 0 .113-.005.171-.014.075-.013.14-.047.208-.076.03-.013.062-.018.091-.033a1 1 0 0 0 .315-.266c.006-.007.015-.01.021-.017l31-42a1 1 0 0 0 .076-1.068l-7.001-13zM8.107 9.911L15.091 20H2.674L8.107 9.911zM33 22h12.581L33 57.226V22zm.789-2l6.153-9.998L45.326 20H33.789zM32 19.092L25.789 9H38.21L32 19.092zM30.211 20H18.674l5.383-9.998L30.211 20zM31 22v35.227L18.419 22H31zm-14.705 0l12.482 34.949L2.981 22h13.314zm31.41 0h13.314L35.223 56.949 47.705 22zm1.204-2l6.985-10.089L61.326 20H48.909zm-1.802-.911L41.674 9h12.417l-6.984 10.089zM22.326 9l-5.433 10.089L9.909 9h12.417zM59 0h2v3h-2zM59 5h2v3h-2zM61 3h3v2h-3zM56 3h3v2h-3z"/>
    </svg>
  </div>
</main>
</body>
</html>
