# DOCS_SKILL — Viết docs hệ thống: .md → .html

`.md` = nguồn sự thật (agent đọc hiểu + phát triển chức năng). `.html` = visualize `.md` cho developer. Portable qua mọi dự án Unity.

## Quy trình

1. Đọc **tất cả** source files → hiểu 100% data flow, lifecycle, lý do mỗi quyết định
2. Viết `.md` cùng thư mục hệ thống — đầy đủ, chính xác 100%
3. Tạo `.html` từ `.md` — 100% nội dung từ `.md`, không thêm không bớt

---

# Phần A — File .md

**Mục đích:** Agent đọc để hiểu hệ thống nhanh nhất và phát triển chức năng mới. Đồng thời là nguồn nội dung duy nhất cho `.html`.

## Cấu trúc

Sections theo **data flow** (input → processing → output), KHÔNG theo "lý thuyết → thiết kế → code". Chọn sections phù hợp hệ thống:

- **Data structures** — layout, alignment, packing, serialization
- **Core algorithm** — thuật toán chính, bản chất toán học, pipeline xử lý
- **Lifecycle** — per-frame / per-request / per-event flow (pseudocode / diagram)
- **Implementation details** — shader, networking, serialization, API specifics
- **Framework integration** — cách kết nối với engine / external systems
- **Design decisions** — trade-offs, so sánh alternatives
- **Safety / error handling** — defensive checks, edge cases
- **Platform issues** — cross-platform fixes, workarounds
- **Architecture** — file tree, dependency graph, file roles
- **Testing** — test checklist + debug hints
- **Extension** — roadmap
- **Performance summary** — metrics bảng (luôn ở cuối)

## Quy tắc

1. **1 khái niệm = 1 giải thích** tại chỗ xuất hiện lần đầu
2. **Giải thích bản chất** — toán, cơ chế bên dưới, tại sao tối ưu. Không chỉ "dùng X"
3. **So sánh ≥2 lựa chọn → bảng** — trade-offs, ✓ đánh dấu lựa chọn hiện tại
4. **Data flow → ASCII diagram**
5. **Code trích nguyên văn** khi cần — không viết lại
6. **Mỗi quyết định có "tại sao"** — "dùng A vì B, không C vì D"
7. **Section cuối = bảng metrics** — con số cụ thể

---

# Phần B — File .html

**Mục đích:** Developer đọc trên browser — visualize `.md` với demos, syntax highlighting, thẩm mỹ tốt.

## Tư tưởng

- Sections giữ nguyên cấu trúc `.md`. Code nguyên văn, HTML entities (`&lt;` `&gt;` `&amp;`)
- Header tĩnh + TOC. Không hero animation. Section cuối = `.perf-grid`
- **Visual ưu tiên**: so sánh → bảng · data flow → `.arch` · giá trị liên tục → Canvas · nhiều bước → step animation. Demo chỉ khi text không đủ
- **Thẩm mỹ**: dark theme, `.reveal` fade-in, card/note/table/`.arch`/`.demo`/`.perf-grid`. Responsive
- **Zero idle cost**: không rAF loop chạy mãi, mọi demo event-driven hoặc static

## HTML template

```html
<!DOCTYPE html>
<html lang="vi">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>{System} — {Project}</title>
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/themes/prism-tomorrow.min.css">
<style>
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
:root{--bg:#0d1117;--bg2:#161b22;--bg3:#1c2333;--bg4:#242d3a;--tx:#e6edf3;--tx2:#8b949e;--tx3:#484f58;--ac:#58a6ff;--ac2:#bc8cff;--gr:#3fb950;--or:#d29922;--rd:#f85149;--bd:#30363d;--r:10px;--mono:'JetBrains Mono','Fira Code','Cascadia Code','SF Mono',monospace;--sans:-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif}
html{scroll-behavior:smooth;background:var(--bg);color:var(--tx);font:17px/1.7 var(--sans)}
body{overflow-x:hidden}
a{color:var(--ac);text-decoration:none}a:hover{text-decoration:underline}
#progress{position:fixed;top:0;left:0;height:3px;background:linear-gradient(90deg,var(--ac),var(--ac2));width:0;z-index:999;transition:width .1s}
#header{max-width:920px;margin:0 auto;padding:4rem 1.5rem 2rem;border-bottom:1px solid var(--bd)}
#header h1{font-size:clamp(2rem,5vw,3rem);font-weight:800;letter-spacing:-.03em;background:linear-gradient(135deg,var(--ac),var(--ac2));-webkit-background-clip:text;-webkit-text-fill-color:transparent}
#header .sub{font-size:1.05rem;color:var(--tx2);margin-top:.4rem}
#header .badge{display:inline-block;margin-top:.8rem;padding:.3em .9em;border:1px solid var(--bd);border-radius:99px;font-family:var(--mono);font-size:.78rem;color:var(--tx2)}
main{max-width:920px;margin:0 auto;padding:0 1.5rem 6rem}
section{padding-top:4rem}
section .reveal{opacity:0;transform:translateY(24px);transition:opacity .6s ease,transform .6s ease}
section .reveal.visible{opacity:1;transform:none}
#toc{max-width:920px;margin:3rem auto;padding:0 1.5rem}
#toc h2{font-size:1.3rem}
#toc ol{columns:2;column-gap:2rem;padding-left:1.5em}
#toc li{font-size:.92rem;margin-bottom:.3rem;break-inside:avoid}
@media(max-width:600px){#toc ol{columns:1}}
h2{font-size:1.9rem;font-weight:700;letter-spacing:-.02em;margin-bottom:1.5rem;padding-bottom:.5rem;border-bottom:1px solid var(--bd)}
h3{font-size:1.3rem;font-weight:600;margin:2.5rem 0 1rem;color:var(--ac)}
p,ul,ol{margin-bottom:1rem}ul{padding-left:1.3em}li{margin-bottom:.3rem}
strong{color:var(--tx);font-weight:600}
em{color:var(--ac2);font-style:normal}
code{font-family:var(--mono);font-size:.88em;background:var(--bg3);padding:.15em .4em;border-radius:4px}
pre{margin:1.2rem 0!important;border-radius:var(--r)!important;border:1px solid var(--bd)!important}
pre code{background:none!important;padding:0!important;font-size:.85rem!important;line-height:1.6!important}
.card{background:var(--bg2);border:1px solid var(--bd);border-radius:var(--r);padding:1.5rem;margin:1.5rem 0}
.card-title{font-weight:600;margin-bottom:.75rem;font-size:1.05rem;color:var(--ac)}
.note{border-left:3px solid var(--or);background:var(--bg2);padding:1rem 1.2rem;border-radius:0 var(--r) var(--r) 0;margin:1.5rem 0;font-size:.92rem;color:var(--tx2)}
.note strong{color:var(--or)}
table{width:100%;border-collapse:collapse;margin:1.2rem 0;font-size:.92rem}
th{text-align:left;padding:.6rem .8rem;border-bottom:2px solid var(--bd);color:var(--ac);font-weight:600}
td{padding:.5rem .8rem;border-bottom:1px solid var(--bd)}
tr:last-child td{border:none}
.demo{background:var(--bg2);border:1px solid var(--bd);border-radius:var(--r);padding:1.5rem;margin:2rem 0}
.demo-title{font-family:var(--mono);font-size:.8rem;color:var(--ac2);text-transform:uppercase;letter-spacing:.08em;margin-bottom:1rem}
.demo canvas{display:block;border-radius:8px;cursor:crosshair}
.demo-row{display:flex;gap:1.5rem;align-items:flex-start;flex-wrap:wrap}
.demo-row>*{flex:1;min-width:250px}
.arch{font-family:var(--mono);font-size:.82rem;line-height:1.8;white-space:pre;overflow-x:auto;padding:1rem;background:var(--bg3);border-radius:var(--r);color:var(--tx2)}
.arch em{color:var(--ac);font-style:normal}
.perf-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:1rem;margin:1.5rem 0}
.perf-card{background:var(--bg3);border-radius:var(--r);padding:1.2rem;text-align:center}
.perf-val{font-size:1.8rem;font-weight:800;background:linear-gradient(135deg,var(--ac),var(--ac2));-webkit-background-clip:text;-webkit-text-fill-color:transparent}
.perf-label{font-size:.82rem;color:var(--tx2);margin-top:.2rem}
footer{text-align:center;padding:3rem 1rem;color:var(--tx3);font-size:.85rem;border-top:1px solid var(--bd)}
</style>
</head>
<body>
<div id="progress"></div>
<header id="header"><h1>{System}</h1><p class="sub">{mô tả}</p><span class="badge">{keywords}</span></header>
<nav id="toc"><h2>Mục lục</h2><ol>...</ol></nav>
<main><!-- sections --></main>
<footer>{Project} · {System} · <code>{Namespace}</code></footer>
<script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/prism.min.js"></script>
<script src="https://cdnjs.cloudflare.com/ajax/libs/prism/1.29.0/components/prism-csharp.min.js"></script>
<script>
const observer=new IntersectionObserver(e=>{e.forEach(e=>{if(e.isIntersecting)e.target.classList.add('visible')})},{threshold:.08});
document.querySelectorAll('.reveal').forEach(el=>observer.observe(el));
const pb=document.getElementById('progress');
window.addEventListener('scroll',()=>{const h=document.documentElement;pb.style.width=(h.scrollTop/(h.scrollHeight-h.clientHeight)*100)+'%'});
</script>
</body>
</html>
```

Thêm Prism components theo nhu cầu: `prism-glsl`, `prism-json`, `prism-python`, ...

## Demo patterns

| Pattern | Khi nào | Perf |
|---------|---------|------|
| **A: Canvas + Mouse** | Giá trị thay đổi theo vị trí | Pre-render bg, rAF guard |
| **B: Step/Auto** | Quá trình rời rạc | DOM-only, `clearInterval` on reset, ≥800ms |
| **C: Input → Transform** | Chuyển đổi real-time | Compute rẻ → trực tiếp handler |
| **D: Static Graph** | Hàm toán học | IIFE 1 lần, không listener |

Mỗi demo: IIFE wrap, cache `getElementById` đầu IIFE, `addEventListener` (không `onclick`).

## Hiệu năng .html — blacklist

| Cấm trong draw loop / handler | Tại sao | Thay bằng |
|-------------------------------|---------|-----------|
| `ctx.shadowBlur` | Gaussian blur per draw | Radial gradient |
| `createImageData()` per event | Alloc W×H×4 bytes | Tạo 1 lần, reuse |
| Per-pixel math per mousemove | O(W×H) 60+/giây | Pre-render → cached ImageData |
| `mousemove` → draw trực tiếp | Draw 2–3× giữa 2 frame | rAF guard: scalar coords + dirty flag |
| `ctx.fillStyle = 'var(--x)'` | Canvas không parse CSS vars | Hex: `'#8b949e'` |
| `innerHTML` tight loop | Parser + reflow | `textContent` |
| Quên `cancelAnimationFrame` | rAF chạy sau leave | Cancel trong leave handler |

Pattern A mẫu (pre-render + rAF guard):

```js
(function() {
  const canvas = document.getElementById('demoCanvas');
  const ctx = canvas.getContext('2d');
  const W = canvas.width, H = canvas.height;

  // Pre-render background ONCE
  const bg = ctx.createImageData(W, H);
  for (let y = 0; y < H; y++)
    for (let x = 0; x < W; x++) {
      const i = (y * W + x) * 4;
      bg.data[i]=/*R*/; bg.data[i+1]=/*G*/; bg.data[i+2]=/*B*/; bg.data[i+3]=255;
    }

  function draw(mx, my) { ctx.putImageData(bg, 0, 0); /* cheap overlay */ }
  draw(-1, -1);

  let px=-1, py=-1, dirty=false, raf=0;
  function tick() { raf=0; if(dirty){draw(px,py); dirty=false;} }
  canvas.addEventListener('mousemove', e => {
    const r=canvas.getBoundingClientRect();
    px=(e.clientX-r.left)*W/r.width; py=(e.clientY-r.top)*H/r.height;
    dirty=true; if(!raf) raf=requestAnimationFrame(tick);
  });
  canvas.addEventListener('mouseleave', () => {
    dirty=false; if(raf){cancelAnimationFrame(raf);raf=0;} draw(-1,-1);
  });
})();
```

---

## Checklist

**File .md:**
- [ ] Data flow structure. 1 khái niệm = 1 nơi. Mỗi quyết định có "tại sao"
- [ ] So sánh → bảng. Pipeline → ASCII diagram. Metrics bảng cuối
- [ ] Đủ thông tin để `.html` trình bày 100% mà không cần đọc source

**File .html:**
- [ ] 100% nội dung từ `.md`. Single file, no build, header tĩnh, TOC khớp, responsive
- [ ] Code nguyên văn, Prism class, HTML entities. `.reveal` trên content elements
- [ ] Zero idle cost. Canvas: pre-render, rAF guard, scalar, cancel, hex. Không allocate/shadowBlur/filter trong loop
