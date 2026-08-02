# Scriptorium

**Ask questions about your own documents — entirely on your own machine.**

A local ASP.NET Core Razor Pages app. Build a library of searchable indexes over folders of PDFs
and text files, then ask questions in plain English. Answers come from a local Ollama model and
cite the pages they came from.

Named for the room in a monastery where manuscripts were copied and kept — which is roughly what
this does, if rather faster and with worse handwriting.

**Nothing leaves your machine.** No API keys, no accounts, no cloud service — the app talks only
to Ollama on `localhost`. That is also why there is nothing to put in user secrets.

---

## Quick start

**1. Install [Ollama](https://ollama.com) and pull two models:**

```bash
ollama pull snowflake-arctic-embed:33m
```

```bash
ollama pull gemma4:12b
```

Two models are needed because they do different jobs. The *embedding* model turns text into
vectors for search; the *chat* model writes prose. An embedding model can't chat, and using a
12B chat model to embed thousands of chunks would be enormously slower.

Any chat model works — `gemma4:12b` is a reasonable starting point. RAG doesn't need a large one,
because the relevant passages are handed to it directly; a model that fits entirely in VRAM
answers far faster than a bigger one that spills onto the CPU.

**2. Set the chat model you pulled** in the `Rag` section of
[`RAG/appsettings.json`](RAG/appsettings.json), replacing whatever is there:

```json
"ChatModel": "gemma4:12b"
```

**3. Run it:**

```bash
dotnet run --project RAG
```

Open the URL it prints. The page checks Ollama on load and tells you plainly if a model is
missing or the server isn't up.

**4. Create a dataset**, click **Add to this index…**, browse to a folder of documents, and hit
**Index folder**.

**5. Ask a question.** The answer streams in with `[1]`, `[2]` citations; expand *Sources* to see
the exact passage the model was given and which page it came from.

---

## Where your documents and indexes live

Everything the app builds goes in one place: the **library folder**, `DataSets` at the root of
the repository (alongside the `RAG` project folder).

```
DataSets/
  Family History/
    rag-index.db      the searchable index
    dataset.json      display name, description, counts, which model built it
  Legal Contracts/
    rag-index.db
    dataset.json
```

**You do not have to create this folder** — the app creates it on first run. To put it somewhere
else (a different drive, a NAS, outside the repo), change one setting:

```json
"DataSetsRoot": "D:\\MyIndexes"
```

Relative paths resolve against the app's content root, so the default `"..\\DataSets"` means
"one level up from the `RAG` project folder". An absolute path works too.

**The library is excluded from source control** by [`.gitignore`](.gitignore). An index contains
the full text of every document you indexed, so committing one would publish the documents
themselves. Nothing in `DataSets/` needs to be in the repository — the app recreates the folder,
and any index can be rebuilt by re-indexing.

### Datasets are just folders

A dataset is a folder with an index in it, which makes the library easy to manage by hand as well
as through the UI:

- **Back one up** — copy the folder.
- **Move it to another machine** — copy the folder; it arrives with its name and settings intact.
- **Throw one away** — delete the folder.

The UI does all of this too (**New**, **Rename**, **Duplicate**, **Delete**). Deleting moves the
folder to `_deleted-<name>-<timestamp>` rather than erasing it, so a misclick is recoverable.
Folders starting with `_` are working space and never appear in the library.

### One dataset, many folders

A dataset can index as many source folders as you like — a *Family History* dataset might cover
three separate directories. Ask a question against **the whole dataset** or scope it to **one
folder** using the *Search* dropdown.

Documents are keyed on (folder, path), so nested or overlapping folders each keep their own copy
of a shared file rather than stealing it from one another. The cost is that a file covered by two
indexed folders is stored and embedded twice.

Network paths work either way. A UNC path (`\\nas\share\books`) and a mapped drive (`Z:\books`)
measured the same — 96 vs 84 MB/s cold, within noise, since a mapped drive is an alias for the
same SMB redirector. UNC is the more robust choice, because mapped drives are per-user and
per-logon-session. **They are different index keys**, though: switching a folder from `Z:\` to
`\\nas\` means re-indexing it, so pick one before a big run.

---

## Using it

The page is four sections, top to bottom.

**Resources** — collapsed by default; see [below](#the-resources-panel).

**Choose a dataset** — the library. Each row shows file and chunk counts, size on disk and which
embedding model built it. Selecting one loads it and releases the previous one, so only the
dataset you are using holds memory.

**Choose a folder** — hidden until you click **Add to this index…**, since picking a folder only
matters when you are adding one. The picker walks the *server's* file system, because a browser
file dialog never exposes a real folder path to the server. That's fine here: the app only ever
runs on your own machine.

**Build the index** — *Index folder* processes new and changed files only, so re-indexing later is
near-instant. *Rebuild from scratch* forces a full re-read of the selected folder. *Remove folder
from dataset* drops one folder and leaves the rest of the dataset alone.

To re-index a folder already in the dataset, click it in the dataset's folder list — no need to
reopen the picker.

**Ask a question** — shows which dataset you are querying, and lets you scope to one folder. Once
an answer lands you get two buttons in the footer:

- **Save** downloads a plain-text transcript — dataset, question, answer, and every citation with
  its page, score, full path and quoted passage. It stands on its own without the app.
- **Clear answer** clears the answer and citations but keeps the question, so you can reword it.

Retrieval finishes in about a second, but the chat model then has to read the whole prompt before
it can write anything, so the first token takes several seconds. A ticking spinner shows what it
is waiting on, and a footer reports tokens, time to first token and tokens/sec.

**Time to first token is `TopK × MaxChunkChars` divided by the model's prompt-processing rate.**
Measured with a 12B model fully in VRAM: ~285 tokens/sec of prompt, so five 2,500-character
passages (~2,550 tokens) cost about 9 seconds before a single word appears. Generation runs at
~38 tok/s. If first-token latency bothers you more than breadth of context, lowering `TopK` is the
direct lever — each passage costs roughly 1.8 seconds.

Changing `NumCtx` forces Ollama to reload the model (~10s) and does **not** affect the
prompt-processing rate, so it is not a latency lever.

Supported file types: `.pdf`, `.txt`, `.md`, `.markdown`, `.csv`, `.log`.

---

## How the RAG part works

An LLM can't read a whole folder of PDFs — it doesn't fit in the context window, and padding the
prompt with irrelevant text makes answers worse. **Retrieval-Augmented Generation** searches
first and shows the model only the handful of passages that matter.

**Indexing (once per folder):**

1. **Extract** — pull text out of each file, page by page (`DocumentTextExtractor`).
2. **Chunk** — cut it into overlapping pieces (`TextChunker`). Overlap means a fact straddling a
   boundary is still whole somewhere.
3. **Embed** — send each chunk to the embedding model, which returns a vector representing its
   meaning (`OllamaClient.EmbedAsync`). `snowflake-arctic-embed:33m` returns 384 numbers.
4. **Store** — save chunks and vectors in SQLite (`VectorStore`).

**Querying (per question):**

5. **Embed the question** the same way, with the query prefix the model expects.
6. **Search** — score every stored vector against it and keep the best matches. Vectors are
   normalised, so cosine similarity is just a dot product (`VectorStore.Search`).
7. **Generate** — paste those passages into a prompt and ask the chat model to answer using only
   them, citing `[1]`, `[2]` (`RagService`). The answer streams back token by token.

---

## Protecting the index

An index can represent hours of embedding, so two things guard it.

**Journals are checked before the database is opened.** SQLite's write-ahead log (`-wal`) belongs
to exactly one database, but nothing in the file format ties the two together — so a `-wal` from a
*different* index sitting next to yours will be replayed on open and can destroy it. That failure
is silent and total: it presents as "database disk image is malformed", and the first read-write
open truncates the file.

`DatabaseGuard` inspects every database first. If a journal would *shrink* the database, the app
refuses to open it and offers to quarantine the journal instead (moved aside, never deleted). The
app also checkpoints journals away whenever it finishes with a dataset, so an idle dataset folder
is left clean with nothing for a later open to trip over.

**Embedding-model mismatches are reported, not silently wrong.** Vectors from two different
models aren't comparable, so querying an index built with another model would quietly match
nothing. The dataset records which model built it, and the app says so instead.

---

## Tuning

Everything lives in the `Rag` section of `appsettings.json`:

| Setting | What it does |
|---|---|
| `DataSetsRoot` | Where the library lives. Relative to the app's content root, or absolute |
| `ChatModel` / `EmbeddingModel` | Which Ollama models to use |
| `MaxChunkChars` / `ChunkOverlapChars` | Chunk size. Smaller = more precise retrieval, less surrounding context |
| `TopK` | How many passages go into the prompt. More context, slower answers |
| `MinScore` | Cosine floor. Raise it to cut weak matches, lower it if good passages get dropped |
| `NumCtx` | Ollama context window. Must fit `TopK` chunks plus the answer |
| `Temperature` | Kept low (0.2) so the model sticks to the source text |
| `Think` | `true` makes a reasoning model think before answering — better on hard questions, much slower |
| `SearchRecursively` | Whether subfolders are included |
| `MaxCachedVectors` | Above this many chunks, search streams from disk instead of caching in RAM |
| `EmbedDocumentPrefix` / `EmbedQueryPrefix` | Task prefixes. **Change these with the model** |

Embedding models are trained with their own task prefixes, and the wrong ones cost retrieval
quality. `arctic-embed` wants a bare document and a prefixed query; `nomic-embed-text` wants
`"search_document: "` and `"search_query: "`.

**If answers are wrong or say "not in the documents"** — expand *Sources* first. If the right
passage isn't listed, retrieval is the problem: raise `TopK` or lower `MinScore`. If the right
passage *is* listed but the answer is still poor, that's the chat model.

---

## The Resources panel

It samples every 3 seconds while open and stops when closed.

- **This app** — process memory, how much of that is cached vectors, and CPU.
- **Machine** — free system RAM, and VRAM per GPU (via `nvidia-smi`, skipped silently if absent).
- **Ollama models loaded** — from `/api/ps`. The number that matters is **percent on GPU**.

**Percent on GPU is the single best predictor of answer speed.** If a model is larger than your
VRAM, Ollama runs the overflow layers on the CPU, several times slower per token. A model showing
48% on GPU is doing roughly half its work the slow way. The panel warns when this happens.

The fix is a model that fits in VRAM — RAG doesn't need a huge model, because the relevant
passages are handed to it directly. Lowering `NumCtx` also frees VRAM, since the context window's
KV cache competes with model weights for the same memory.

---

## Memory and scale

Search never loads chunk *text* into memory — only vectors, and only the winning handful of chunks
have their text read back from SQLite.

Vectors are held in one contiguous array rather than one array per chunk, which avoids per-object
overhead and keeps the scan sequential. Scoring uses SIMD, so it handles 8–16 floats per
instruction, and only the top K scores are kept rather than sorting the whole index.

At 384 dimensions each chunk costs 1.5 KB of RAM when cached. A 870,000-chunk library is about
1.3 GB. Past `MaxCachedVectors` chunks, vectors are **not** cached — they stream from disk and are
scored as they arrive. Slower per query, but memory stays flat and a huge dataset can't crash the
app. The dataset line tells you which mode is in use.

The default of 2,500,000 chunks is roughly 3.8 GB of vectors, which suits a workstation with
plenty of RAM. Lower it on a smaller machine: 300,000 ≈ 460 MB. The cache is one contiguous
allocation built lazily on the first question — if there isn't enough memory it throws there, and
lowering this value is the fix.

Only the dataset you are using is held open. Switching datasets releases the previous one, so two
large libraries never stack up in memory.

---

## OCR for scanned books

Old books are often scans — images of text with no text layer — and PDF text extraction gets
nothing from them. Any page yielding fewer than `OcrMinCharsPerPage` characters is rendered to a
bitmap (PDFium, via PDFtoImage) and passed to OCR.

The engine is the one **built into Windows** (`Windows.Media.Ocr`), so there is nothing to install
and no language data to download. That is why the project targets a Windows TFM.

- Only text-less pages are OCR'd. Pages with a real text layer are left alone — OCR would be
  slower and usually worse.
- OCR output below `OcrMinCharsPerPage` is discarded. This matters: a 17th-century emblem book
  produced things like `"quircndz sima SercniS//no/"`, and indexing that would pollute search.
- Measured ~5.5 pages/sec at 300 DPI with 4 workers. Throughput plateaus around 4 because
  rasterising, not recognition, is the limiting step.

Measured on a 923-page 1920 scan: **889 pages recovered, 906 chunks — against 5 chunks without
OCR.** Occasional artefacts (`di8!rict`) don't stop the embedding model finding the right page.

**What OCR will not fix:** handwriting, calligraphy, and heavily decorative type. Manuscript and
emblem books stay unsearchable.

| Setting | Meaning |
|---|---|
| `EnableOcr` | Master switch |
| `OcrMinCharsPerPage` | Below this a page is considered scanned; also the minimum OCR output kept |
| `OcrDpi` | 300 reads old print noticeably better than 200; beyond that cost outruns accuracy |
| `OcrMaxParallelism` | Pages OCR'd at once (4 is the measured plateau) |
| `OcrLanguage` | BCP-47 tag; falls back to any installed language |

---

## Indexing speed

Embedding is the bottleneck — not disk, not the network, not PDF parsing. Measured on an RTX 5070
against a 2,701-file / 32.6 GB library on a NAS:

- Reading all 32.6 GB over gigabit: ~15 minutes total. Copying the corpus to a local disk saves
  nothing, because the copy costs the same 15 minutes.
- Ollama serialises embedding requests. Going from 1 to 8 concurrent requests moved throughput
  from 23.8 to 30.2 chunks/sec, so parallelising the client side isn't worth the complexity.
- Cost is dominated by a **fixed per-chunk overhead**, not by chunk length: 7× more text per chunk
  cost only 1.4× more time. Fewer, larger chunks is therefore the single biggest lever.

Throughput by model, same hardware:

| Model | @1000 chars | @2500 chars | Dims |
|---|---|---|---|
| `nomic-embed-text` (137M) | 27/s | 16/s | 768 |
| `snowflake-arctic-embed:33m` | 60/s | 41/s | 384 |
| `all-minilm` (23M) | 87/s | 82/s | 384 |

The defaults here (arctic-embed at 2,500 chars) index that library in about 5 hours, against
roughly 20 hours for nomic at 1,000 chars.

**Changing `EmbeddingModel` or the chunk settings requires a full rebuild** — old vectors have a
different dimension and different chunk boundaries, so they can't be compared with new ones. Use
*Rebuild from scratch*, and remember to change the task prefixes to match the new model.

---

## Known limitations

- **Windows-only.** The Windows TFM (for the built-in OCR engine), drive-letter folder browsing
  and the memory counters all assume Windows. Running elsewhere means swapping the OCR engine for
  something like Tesseract.
- **No authentication.** It reads any folder the app's user account can read, so keep it on
  localhost. Do not expose it to a network.
- **Handwriting and calligraphy defeat OCR.** Printed scans come through well; manuscript hands
  and decorative type do not.
- **Search is a brute-force scan** over every vector. That's deliberate — a SIMD dot product over
  384 floats is trivial next to the LLM's response time, and it keeps the code dependency-free.
  Past a few million chunks you'd want a real vector index (sqlite-vec, Qdrant).
- **One indexing run at a time**, across the whole app.

---

## Layout

```
RAG/
  Services/
    RagOptions.cs             configuration
    OllamaClient.cs           HTTP calls to /api/embed and /api/chat
    DocumentTextExtractor.cs  PDF and plain-text extraction
    OcrService.cs             renders text-less PDF pages and OCRs them (Windows.Media.Ocr)
    TextChunker.cs            splitting text into overlapping chunks
    Dataset.cs                dataset record and its on-disk metadata
    DatasetRegistry.cs        the library: create, rename, duplicate, delete
    DatabaseGuard.cs          refuses to open a database a foreign journal would destroy
    VectorStore.cs            SQLite storage + similarity search, one per dataset
    VectorStoreProvider.cs    opens datasets and decides which stay in memory
    IndexingService.cs        background indexing with progress
    RagService.cs             retrieve, build prompt, stream answer
    SystemMetrics.cs          RAM/CPU/GPU and Ollama residency for the Resources panel
  Pages/Index.cshtml(.cs)     the single page and its JSON/SSE handlers
  wwwroot/js/rag.js           library management, progress polling, streamed answers
```

## Licence

[MIT](LICENSE).
