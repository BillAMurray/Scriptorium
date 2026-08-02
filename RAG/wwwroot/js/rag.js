// Front-end for the RAG page: the dataset library, folder browsing, index progress polling,
// and streamed answers.
(() => {
    "use strict";

    const $ = (id) => document.getElementById(id);
    const token = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";

    const el = {
        health: $("health"),
        datasetList: $("datasetList"),
        datasetSummary: $("datasetSummary"),
        datasetDetail: $("datasetDetail"),
        datasetError: $("datasetError"),
        datasetHealth: $("datasetHealth"),
        datasetHealthText: $("datasetHealthText"),
        quarantineBtn: $("quarantineBtn"),
        modelMismatch: $("modelMismatch"),
        addFolderBtn: $("addFolderBtn"),
        newDatasetBtn: $("newDatasetBtn"),
        renameDatasetBtn: $("renameDatasetBtn"),
        duplicateDatasetBtn: $("duplicateDatasetBtn"),
        deleteDatasetBtn: $("deleteDatasetBtn"),
        copyWrap: $("copyWrap"),
        copyBar: $("copyBar"),
        copyText: $("copyText"),
        sectionFolder: $("sectionFolder"),
        folderInput: $("folderInput"),
        folderStats: $("folderStats"),
        indexTarget: $("indexTarget"),
        browseBtn: $("browseBtn"),
        browser: $("browser"),
        browsePath: $("browsePath"),
        browseList: $("browseList"),
        browseUpBtn: $("browseUpBtn"),
        browseUseBtn: $("browseUseBtn"),
        indexBtn: $("indexBtn"),
        rebuildBtn: $("rebuildBtn"),
        clearBtn: $("clearBtn"),
        cancelBtn: $("cancelBtn"),
        progressWrap: $("progressWrap"),
        progressBar: $("progressBar"),
        progressText: $("progressText"),
        warnings: $("warnings"),
        indexError: $("indexError"),
        docsDetails: $("docsDetails"),
        docsList: $("docsList"),
        scopeSelect: $("scopeSelect"),
        askScopeSummary: $("askScopeSummary"),
        question: $("question"),
        askBtn: $("askBtn"),
        stopBtn: $("stopBtn"),
        askStage: $("askStage"),
        answerWrap: $("answerWrap"),
        answer: $("answer"),
        sourcesWrap: $("sourcesWrap"),
        sources: $("sources"),
        sourcesCount: $("sourcesCount"),
        askDataset: $("askDataset"),
        askError: $("askError"),
        sectionIndex: $("sectionIndex"),
        indexSummary: $("indexSummary")
    };

    let currentDataset = null;
    let currentDatasetName = null;

    // Everything the last completed answer needs to be written out as a transcript.
    let lastResult = null;
    let lastSources = [];

    let browsePath = null;
    let pollTimer = null;
    let copyTimer = null;
    let askController = null;

    const escapeHtml = (s) => String(s).replace(/[&<>"']/g, (c) =>
        ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

    const show = (node, visible) => node.classList.toggle("d-none", !visible);

    const number = (n) => (n ?? 0).toLocaleString();

    // ---------- the folder picker ----------

    // Kept out of the way until adding a folder is the task at hand. With a dataset selected the
    // interesting actions are asking and re-indexing, and a permanently visible picker reads as a
    // step you are supposed to complete.
    function revealFolderPicker() {
        show(el.sectionFolder, true);
        el.sectionFolder.open = true;
        el.folderInput.focus();
    }

    function hideFolderPicker() {
        show(el.sectionFolder, false);
        el.sectionFolder.open = false;
        show(el.browser, false);
    }

    /// Spells out what the index buttons will act on, now that the folder is not always on screen.
    function updateIndexTarget() {
        const folder = el.folderInput.value.trim();

        el.indexTarget.innerHTML = folder
            ? `Folder: <code>${escapeHtml(folder)}</code>`
            : `No folder chosen — pick one from the dataset's folder list, or use ` +
              `<strong>Add to this index…</strong>`;

        // Keep the dataset's folder list in step with what is selected.
        el.datasetDetail?.querySelectorAll(".dataset-folder").forEach((row) =>
            row.classList.toggle("selected", row.dataset.folder === folder.toLowerCase()));
    }

    async function postJson(handler, body) {
        const response = await fetch(`?handler=${handler}`, {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token() },
            body: JSON.stringify(body ?? {})
        });
        return response.json();
    }

    async function getJson(handler, params = {}) {
        const query = new URLSearchParams({ handler, ...params });
        const response = await fetch(`?${query}`);
        return response.json();
    }

    // ---------- Ollama health ----------

    async function checkHealth() {
        try {
            const h = await getJson("Health");

            if (!h.reachable) {
                el.health.className = "alert alert-danger py-2 small";
                el.health.innerHTML = `<strong>Ollama is not reachable.</strong> ${escapeHtml(h.error ?? "")}
                    <br>Start it with <code>ollama serve</code>, then reload this page.`;
                return;
            }

            const missing = [];
            if (!h.chatModelReady) missing.push(h.chatModel);
            if (!h.embeddingModelReady) missing.push(h.embeddingModel);

            if (missing.length) {
                el.health.className = "alert alert-warning py-2 small";
                el.health.innerHTML = `Ollama is running, but these models are not pulled: ` +
                    missing.map((m) => `<code>${escapeHtml(m)}</code>`).join(", ") +
                    `<br>Pull them with ` +
                    missing.map((m) => `<code>ollama pull ${escapeHtml(m)}</code>`).join(" and ") + `.`;
                return;
            }

            el.health.className = "alert alert-success py-2 small";
            el.health.innerHTML = `Ollama ready — answering with <code>${escapeHtml(h.chatModel)}</code>, ` +
                `embedding with <code>${escapeHtml(h.embeddingModel)}</code>.` +
                `<br>OCR for scanned pages: ${h.ocrAvailable ? "" : "<strong>off</strong> — "}${escapeHtml(h.ocr)}`;
        } catch (e) {
            el.health.className = "alert alert-danger py-2 small";
            el.health.textContent = `Could not check Ollama: ${e.message}`;
        }
    }

    // ---------- The library ----------

    function datasetError(message) {
        if (!message) {
            show(el.datasetError, false);
            return;
        }
        el.datasetError.textContent = message;
        show(el.datasetError, true);
    }

    async function loadDatasets() {
        let data;
        try {
            data = await getJson("Datasets");
        } catch (e) {
            el.datasetList.innerHTML = `<div class="text-danger p-2">${escapeHtml(e.message)}</div>`;
            return;
        }

        renderCopyStatus(data.copy);

        if (!data.datasets.length) {
            el.datasetList.innerHTML =
                `<div class="text-muted p-2">No datasets yet — create one to get started.</div>`;
            el.datasetSummary.textContent = "empty library";
            currentDataset = null;
            renderDetail(null);
            return;
        }

        // Fall back to the first dataset if the remembered one has been deleted or renamed away.
        const saved = localStorage.getItem("ragDataset");
        if (!data.datasets.some((d) => d.id === currentDataset)) {
            currentDataset = data.datasets.some((d) => d.id === saved) ? saved : data.datasets[0].id;
        }

        el.datasetList.innerHTML = data.datasets.map((d) => {
            const selected = d.id === currentDataset;
            // An index adopted from before the library existed has no stored counts until it is
            // opened — saying so beats showing a confident zero.
            const facts = d.countsKnown
                ? [
                    `${number(d.documentCount)} file${d.documentCount === 1 ? "" : "s"}`,
                    `${number(d.chunkCount)} chunks`,
                    `${number(d.sizeMb)} MB`
                  ]
                : [`${number(d.sizeMb)} MB`, `open to count`];

            if (d.embeddingModel) facts.push(escapeHtml(d.embeddingModel));

            const flags = [];
            if (!d.healthy) flags.push(`<span class="badge bg-danger">needs attention</span>`);
            if (d.indexing) flags.push(`<span class="badge bg-primary">indexing</span>`);

            return `<button type="button" class="dataset-item${selected ? " selected" : ""}" data-id="${escapeHtml(d.id)}">
                <span class="dataset-name">${escapeHtml(d.displayName)} ${flags.join(" ")}</span>
                <span class="dataset-facts">${facts.join(" · ")}</span>
                ${d.description ? `<span class="dataset-desc">${escapeHtml(d.description)}</span>` : ""}
            </button>`;
        }).join("");

        el.datasetList.querySelectorAll(".dataset-item").forEach((button) =>
            button.addEventListener("click", () => selectDataset(button.dataset.id)));

        el.datasetSummary.textContent =
            `${data.datasets.length} dataset${data.datasets.length === 1 ? "" : "s"}`;

        await loadDatasetDetail();
    }

    async function selectDataset(id) {
        if (id === currentDataset) return;

        currentDataset = id;
        localStorage.setItem("ragDataset", id);

        // The folder selection belongs to the dataset, not to the page.
        el.folderInput.value = localStorage.getItem(`ragFolder:${id}`) ?? "";

        // Switching datasets ends whatever add-a-folder flow was in progress.
        hideFolderPicker();

        el.datasetList.querySelectorAll(".dataset-item").forEach((button) =>
            button.classList.toggle("selected", button.dataset.id === id));

        await loadDatasetDetail();
        updateIndexTarget();
        refreshStatus();
    }

    async function loadDatasetDetail() {
        if (!currentDataset) {
            renderDetail(null);
            return;
        }

        localStorage.setItem("ragDataset", currentDataset);

        let d;
        try {
            d = await getJson("Dataset", { dataset: currentDataset });
        } catch (e) {
            datasetError(e.message);
            return;
        }

        if (d.error) {
            renderDetail(null);
            show(el.datasetHealth, d.canQuarantine === true);
            el.datasetHealthText.textContent = d.error;
            if (!d.canQuarantine) datasetError(d.error);
            return;
        }

        datasetError(null);
        show(el.datasetHealth, false);
        renderDetail(d);
    }

    function renderDetail(d) {
        if (!d) {
            currentDatasetName = null;
            el.datasetDetail.innerHTML = "";
            el.scopeSelect.innerHTML = `<option value="">The whole dataset</option>`;
            el.askDataset.innerHTML = `<span class="ask-dataset-label">No dataset selected</span>`;
            show(el.modelMismatch, false);
            el.askScopeSummary.textContent = "";
            return;
        }

        currentDatasetName = d.displayName;

        el.askDataset.innerHTML =
            `<span class="ask-dataset-label">Dataset:</span> ` +
            `<span class="ask-dataset-name">${escapeHtml(d.displayName)}</span>`;

        show(el.modelMismatch, d.modelMismatch === true);
        if (d.modelMismatch) {
            el.modelMismatch.innerHTML =
                `This dataset was indexed with <code>${escapeHtml(d.embeddingModel)}</code>, which is not the ` +
                `embedding model currently configured. Questions will not match anything until you either ` +
                `switch back to that model or rebuild the dataset from scratch.`;
        }

        const facts = [
            `${number(d.documentCount)} file${d.documentCount === 1 ? "" : "s"}`,
            `${number(d.chunkCount)} chunks`,
            `${number(d.sizeMb)} MB`
        ];
        if (d.searchMode) facts.push(`searched ${escapeHtml(d.searchMode)}`);
        if (d.embeddingDimension) facts.push(`${d.embeddingDimension}-dim vectors`);

        const folders = d.folders ?? [];

        el.datasetDetail.innerHTML =
            `<div class="mb-2">${facts.join(" · ")}</div>` +
            `<div class="mb-1"><code class="small">${escapeHtml(d.databasePath)}</code></div>` +
            (folders.length
                ? `<div class="dataset-folders">` + folders.map((f) =>
                    `<button type="button" class="dataset-folder" data-folder="${escapeHtml(f.folder)}"
                             title="Select this folder for re-indexing">
                        <span class="text-truncate">
                            ${f.onDisk ? "📁" : "⚠️"} ${escapeHtml(f.folder)}
                        </span>
                        <span class="text-muted ms-2">${number(f.documentCount)} files · ${number(f.chunkCount)} chunks</span>
                    </button>`).join("") + `</div>`
                : `<div class="text-muted">No folders indexed into this dataset yet.</div>`);

        // Picking a folder here is the way to re-index one without reopening the picker.
        el.datasetDetail.querySelectorAll(".dataset-folder").forEach((row) =>
            row.addEventListener("click", () => {
                el.folderInput.value = row.dataset.folder;
                saveFolder();
                updateIndexTarget();
                refreshStatus();
            }));

        // Rebuild the ask-scope list, keeping the current choice if it survived.
        const previous = el.scopeSelect.value;
        el.scopeSelect.innerHTML =
            `<option value="">The whole dataset (${number(d.chunkCount)} chunks)</option>` +
            folders.map((f) =>
                `<option value="${escapeHtml(f.folder)}">${escapeHtml(f.folder)} (${number(f.chunkCount)} chunks)</option>`).join("");

        if (folders.some((f) => f.folder === previous)) el.scopeSelect.value = previous;
        updateScopeSummary();
        updateIndexTarget();
    }

    function updateScopeSummary() {
        el.askScopeSummary.textContent = el.scopeSelect.value
            ? `scoped to one folder`
            : `whole dataset`;
    }

    el.scopeSelect.addEventListener("change", updateScopeSummary);

    // ---------- Library management ----------

    el.addFolderBtn.addEventListener("click", () => {
        if (!currentDataset) return datasetError("Select a dataset first.");
        datasetError(null);
        revealFolderPicker();
    });

    el.newDatasetBtn.addEventListener("click", async () => {
        const name = prompt("Name for the new dataset:");
        if (!name) return;

        const result = await postJson("CreateDataset", { name });
        if (result.error) return datasetError(result.error);

        currentDataset = result.id;
        localStorage.setItem("ragDataset", result.id);
        el.folderInput.value = "";
        datasetError(null);
        await loadDatasets();

        // A brand new dataset is empty, so choosing its first folder is the only sensible next step.
        revealFolderPicker();
    });

    el.renameDatasetBtn.addEventListener("click", async () => {
        if (!currentDataset) return datasetError("Select a dataset first.");

        const name = prompt("New name for this dataset:");
        if (!name) return;

        const result = await postJson("RenameDataset", { dataset: currentDataset, name });
        if (result.error) return datasetError(result.error);

        datasetError(null);
        await loadDatasets();
    });

    el.duplicateDatasetBtn.addEventListener("click", async () => {
        if (!currentDataset) return datasetError("Select a dataset first.");

        const name = prompt("Name for the copy:");
        if (!name) return;

        const result = await postJson("DuplicateDataset", { dataset: currentDataset, name });
        if (result.error) return datasetError(result.error);

        datasetError(null);
        startCopyPolling();
    });

    el.deleteDatasetBtn.addEventListener("click", async () => {
        if (!currentDataset) return datasetError("Select a dataset first.");

        const confirmed = confirm(
            `Delete the dataset "${currentDataset}"?\n\n` +
            `The folder is moved to a "_deleted-…" folder in the library rather than erased, ` +
            `so it can still be recovered by hand.`);
        if (!confirmed) return;

        const result = await postJson("DeleteDataset", { dataset: currentDataset });
        if (result.error) return datasetError(result.error);

        currentDataset = null;
        localStorage.removeItem("ragDataset");
        datasetError(null);
        await loadDatasets();
    });

    el.quarantineBtn.addEventListener("click", async () => {
        const result = await postJson("QuarantineJournal", { dataset: currentDataset });
        if (result.error) return datasetError(result.error);

        show(el.datasetHealth, false);
        await loadDatasets();
    });

    function renderCopyStatus(copy) {
        const running = copy && copy.state === "Running";
        show(el.copyWrap, running || (copy && copy.state === "Failed"));

        if (!copy) return;

        if (running) {
            el.copyBar.style.width = `${copy.percentComplete}%`;
            el.copyBar.textContent = `${copy.percentComplete}%`;
            el.copyText.textContent = `Copying ${copy.source} → ${copy.target}…`;
        } else if (copy.state === "Failed") {
            el.copyBar.style.width = "100%";
            el.copyBar.classList.add("bg-danger");
            el.copyText.textContent = `Copy failed: ${copy.error ?? ""}`;
        }
    }

    function startCopyPolling() {
        if (copyTimer) return;
        copyTimer = setInterval(async () => {
            const data = await getJson("Datasets");
            renderCopyStatus(data.copy);

            if (data.copy.state !== "Running") {
                clearInterval(copyTimer);
                copyTimer = null;
                await loadDatasets();
            }
        }, 500);
    }

    // ---------- Folder browser ----------

    async function loadBrowser(path) {
        const data = await getJson("Browse", path ? { path } : {});

        if (data.error) {
            el.browseList.innerHTML = `<div class="text-danger p-2">${escapeHtml(data.error)}</div>`;
            return;
        }

        browsePath = data.current;
        el.browsePath.textContent = data.current ?? "This PC";
        el.browseUpBtn.disabled = !data.current;
        el.browseUseBtn.disabled = !data.current;

        const count = data.fileCount ?? null;
        el.browseList.innerHTML = data.folders.length
            ? data.folders.map((f) =>
                `<button type="button" class="browse-item" data-path="${escapeHtml(f.path)}">📁 ${escapeHtml(f.name)}</button>`
              ).join("")
            : `<div class="text-muted p-2">No subfolders.</div>`;

        if (count !== null) {
            el.browseList.insertAdjacentHTML("afterbegin",
                `<div class="small text-muted p-2 border-bottom">${count} indexable file${count === 1 ? "" : "s"} here</div>`);
        }

        el.browseList.querySelectorAll(".browse-item").forEach((btn) =>
            btn.addEventListener("click", () => loadBrowser(btn.dataset.path)));
    }

    el.browseBtn.addEventListener("click", () => {
        const hidden = el.browser.classList.contains("d-none");
        show(el.browser, hidden);
        if (hidden) loadBrowser(el.folderInput.value.trim() || null);
    });

    el.browseUpBtn.addEventListener("click", async () => {
        const data = await getJson("Browse", browsePath ? { path: browsePath } : {});
        loadBrowser(data.parent ?? null);
    });

    el.browseUseBtn.addEventListener("click", () => {
        if (!browsePath) return;
        el.folderInput.value = browsePath;
        show(el.browser, false);
        saveFolder();
        updateIndexTarget();
        refreshStatus();

        // The folder is chosen; indexing it is what comes next.
        el.sectionIndex.open = true;
    });

    // ---------- Folder persistence ----------

    function saveFolder() {
        if (!currentDataset) return;
        localStorage.setItem(`ragFolder:${currentDataset}`, el.folderInput.value.trim());
    }

    el.folderInput.addEventListener("change", () => { saveFolder(); updateIndexTarget(); refreshStatus(); });

    // ---------- Indexing ----------

    async function startIndex(rebuild) {
        if (!currentDataset) {
            el.indexError.textContent = "Choose a dataset first.";
            show(el.indexError, true);
            return;
        }

        const folder = el.folderInput.value.trim();
        show(el.indexError, false);

        const result = await postJson("StartIndex", { dataset: currentDataset, folder, rebuild });
        if (!result.started) {
            el.indexError.textContent = result.error;
            show(el.indexError, true);
            if (result.canQuarantine) {
                el.datasetHealthText.textContent = result.error;
                show(el.datasetHealth, true);
            }
            return;
        }

        show(el.progressWrap, true);
        el.sectionIndex.open = true; // make sure progress is visible even if the section was collapsed
        startPolling();
    }

    el.indexBtn.addEventListener("click", () => startIndex(false));

    el.rebuildBtn.addEventListener("click", () => {
        if (confirm("Delete this folder's index and read every file again?")) startIndex(true);
    });

    el.clearBtn.addEventListener("click", async () => {
        if (!confirm("Remove this folder from the dataset? Other folders in it are unaffected.")) return;

        const result = await postJson("ClearIndex", {
            dataset: currentDataset,
            folder: el.folderInput.value.trim()
        });

        if (result.error) {
            el.indexError.textContent = result.error;
            show(el.indexError, true);
            return;
        }

        refreshStatus();
        loadDatasets();
    });

    el.cancelBtn.addEventListener("click", () => postJson("CancelIndex"));

    function startPolling() {
        if (pollTimer) return;
        pollTimer = setInterval(refreshStatus, 700);
    }

    function stopPolling() {
        clearInterval(pollTimer);
        pollTimer = null;
    }

    let wasRunning = false;

    async function refreshStatus() {
        if (!currentDataset) return;

        const folder = el.folderInput.value.trim();
        let s;
        try {
            s = await getJson("IndexStatus", { dataset: currentDataset, ...(folder ? { folder } : {}) });
        } catch {
            return;
        }

        const running = s.state === "Running";
        show(el.cancelBtn, running);
        el.indexBtn.disabled = running;
        el.rebuildBtn.disabled = running;
        el.clearBtn.disabled = running;
        el.deleteDatasetBtn.disabled = running;
        el.duplicateDatasetBtn.disabled = running;

        if (s.state !== "Idle") {
            show(el.progressWrap, true);
            el.progressBar.style.width = `${s.percentComplete}%`;
            el.progressBar.textContent = `${s.percentComplete}%`;
            el.progressBar.classList.toggle("progress-bar-animated", running);
            el.progressBar.classList.toggle("bg-danger", s.state === "Failed");
            el.progressBar.classList.toggle("bg-success", s.state === "Completed");

            const parts = [`${s.processedFiles}/${s.totalFiles} files`];
            if (s.currentFile) parts.push(`reading ${s.currentFile}`);
            if (s.skippedUnchanged) parts.push(`${s.skippedUnchanged} unchanged`);
            if (s.chunksAdded) parts.push(`${number(s.chunksAdded)} chunks embedded`);
            if (s.ocrPages) parts.push(`${number(s.ocrPages)} pages OCR'd`);
            if (s.removedStale) parts.push(`${s.removedStale} removed`);
            parts.push(`${s.elapsedSeconds}s`);
            if (!running) parts.unshift(s.state);
            el.progressText.textContent = parts.join(" · ");

            // Keep the headline visible in the header for when the section is collapsed.
            el.indexSummary.textContent = running
                ? `${s.percentComplete}% · ${s.processedFiles}/${s.totalFiles} files`
                : `${s.state} · ${s.processedFiles}/${s.totalFiles} files`;
        }

        if (s.error) {
            el.indexError.textContent = s.error;
            show(el.indexError, true);
        }

        if (s.warnings?.length) {
            el.warnings.innerHTML = `<strong>${s.warnings.length} file(s) needed attention:</strong><ul class="mb-0 mt-1">` +
                s.warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join("") + `</ul>`;
            show(el.warnings, true);
        } else {
            show(el.warnings, false);
        }

        if (s.stats) {
            el.folderStats.textContent = s.stats.documentCount
                ? `${number(s.stats.documentCount)} file(s), ${number(s.stats.chunkCount)} chunks` +
                  (s.stats.searchMode ? ` · searched ${s.stats.searchMode}` : "")
                : "not indexed yet";
        } else {
            el.folderStats.textContent = "";
        }

        if (s.documents?.length) {
            el.docsList.innerHTML = s.documents.map((d) =>
                `<div class="d-flex justify-content-between border-bottom py-1">
                    <span class="text-truncate" title="${escapeHtml(d.path)}">${escapeHtml(d.fileName)}</span>
                    <span class="text-muted ms-2">${d.chunkCount} chunks</span>
                 </div>`).join("");
            show(el.docsDetails, true);
        } else {
            show(el.docsDetails, false);
        }

        // A finished run changes the dataset's contents, so the library and the ask-scope list
        // both need to catch up.
        if (wasRunning && !running) loadDatasets();
        wasRunning = running;

        if (!running) stopPolling();
    }

    // ---------- Asking ----------

    function renderAnswer(markdownish) {
        // The model is asked for short prose with [n] citations, so a light touch is enough here.
        let html = escapeHtml(markdownish)
            .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
            .replace(/`([^`]+)`/g, "<code>$1</code>")
            .replace(/^\s*[-*]\s+(.*)$/gm, "<li>$1</li>")
            .replace(/\[(\d+)\]/g, '<sup class="citation">[$1]</sup>')
            .replace(/\n{2,}/g, "</p><p>")
            .replace(/\n/g, "<br>");

        html = html.replace(/(<li>.*<\/li>)/s, "<ul>$1</ul>").replace(/<br>(<li>)/g, "$1");
        return `<p>${html}</p>`;
    }

    // A local model can take 10-30s to produce its first token, mostly reading the prompt.
    // Without a visible, ticking placeholder an empty answer box just looks broken.
    function waitingPlaceholder(seconds, note) {
        return `<div class="answer-waiting">
            <span class="spinner-border spinner-border-sm"></span>
            <span>${escapeHtml(note)}</span>
            <span class="answer-elapsed">${seconds.toFixed(1)}s</span>
        </div>`;
    }

    // ---------- saving and clearing an answer ----------

    /// Plain text rather than markdown or JSON: the point is a file you can read, mail or paste
    /// anywhere years from now without this app being involved.
    function formatTranscript(r) {
        const rule = "=".repeat(74);
        const thin = "-".repeat(74);
        const lines = [
            rule,
            `Dataset:   ${r.dataset}`,
            `Searched:  ${r.scope}`,
            `Asked:     ${r.askedAt}`,
            rule,
            "",
            "QUESTION",
            thin,
            r.question,
            "",
            "ANSWER",
            thin,
            r.answer,
            "",
            `SOURCES (${r.sources.length})`,
            thin,
            ""
        ];

        for (const s of r.sources) {
            lines.push(`[${s.number}] ${s.fileName} — page ${s.pageNumber} — score ${s.score}`);
            lines.push(`     ${s.path}`);
            lines.push("");
            // Indented so the quoted passage is visibly distinct from the citation heading.
            lines.push(...String(s.excerpt).split(/\r?\n/).map((line) => "     " + line));
            lines.push("");
        }

        if (!r.sources.length) lines.push("(no passages matched)", "");

        // CRLF, since these land on a Windows desktop and Notepad still cares.
        return lines.join("\r\n");
    }

    function saveAnswer() {
        if (!lastResult) return;

        const slug = (lastResult.dataset || "dataset")
            .replace(/[^A-Za-z0-9-_]+/g, "-")
            .replace(/-+/g, "-")
            .replace(/^-|-$/g, "");

        const blob = new Blob([formatTranscript(lastResult)], { type: "text/plain;charset=utf-8" });
        const url = URL.createObjectURL(blob);

        const link = document.createElement("a");
        link.href = url;
        link.download = `rag-${slug}-${lastResult.stamp}.txt`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }

    /// Clears the answer and its citations, deliberately leaving the question so it can be reworded.
    function clearAnswer() {
        show(el.answerWrap, false);
        show(el.sourcesWrap, false);
        show(el.askError, false);

        el.answer.innerHTML = "";
        el.sources.innerHTML = "";
        el.sourcesCount.textContent = "";
        el.askStage.textContent = "";

        lastResult = null;
        lastSources = [];
        el.question.focus();
    }

    function statsRow(tokenCount, firstTokenAt, total, rate) {
        return `<div class="answer-stats">
            <span>${tokenCount} tokens · first after ${firstTokenAt.toFixed(1)}s · ` +
            `${total.toFixed(1)}s total · ${rate.toFixed(1)} tok/s</span>
            <span class="answer-actions">
                <button type="button" class="btn btn-sm btn-outline-secondary" data-action="save">Save</button>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-action="clear">Clear answer</button>
            </span>
        </div>`;
    }

    // Delegated, because the stats row is rewritten with every answer.
    el.answer.addEventListener("click", (event) => {
        const action = event.target.closest("[data-action]")?.dataset.action;
        if (action === "save") saveAnswer();
        if (action === "clear") clearAnswer();
    });

    function ask() {
        const q = el.question.value.trim();
        if (!q) return;

        if (!currentDataset) {
            el.askError.textContent = "Choose a dataset first.";
            show(el.askError, true);
            return;
        }

        show(el.askError, false);
        show(el.sourcesWrap, false);
        show(el.answerWrap, true);
        el.answer.innerHTML = "";
        el.sources.innerHTML = "";
        el.askStage.textContent = "";
        el.askBtn.disabled = true;
        show(el.stopBtn, true);

        // Captured now rather than when the answer lands, so a transcript records what was actually
        // asked even if the dataset or scope is changed while the model is still thinking.
        lastResult = null;
        lastSources = [];
        const askedDataset = currentDatasetName ?? currentDataset;
        const askedScope = el.scopeSelect.options[el.scopeSelect.selectedIndex]?.text ?? "The whole dataset";
        const askedAt = new Date();

        let raw = "";
        let note = "Searching the index…";
        let tokenCount = 0;
        let firstTokenAt = null;
        const startedAt = performance.now();
        const elapsed = () => (performance.now() - startedAt) / 1000;

        el.answer.innerHTML = waitingPlaceholder(0, note);
        const ticker = setInterval(() => {
            if (firstTokenAt === null) el.answer.innerHTML = waitingPlaceholder(elapsed(), note);
        }, 100);

        askController = new AbortController();

        const params = { handler: "Ask", dataset: currentDataset, q };
        if (el.scopeSelect.value) params.folder = el.scopeSelect.value;
        const query = new URLSearchParams(params);

        // EventSource can't be aborted cleanly, so the SSE stream is read manually via fetch.
        fetch(`?${query}`, { signal: askController.signal })
            .then(async (response) => {
                if (!response.ok) throw new Error(`Server returned ${response.status}`);

                const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
                let buffer = "";

                for (;;) {
                    const { value, done } = await reader.read();
                    if (done) break;

                    buffer += value;
                    const frames = buffer.split("\n\n");
                    buffer = frames.pop();

                    for (const frame of frames) {
                        const nameLine = frame.match(/^event: (.+)$/m);
                        const dataLine = frame.match(/^data: (.*)$/m);
                        if (!nameLine || !dataLine) continue;

                        const payload = JSON.parse(dataLine[1]);

                        switch (nameLine[1]) {
                            case "stage":
                                note = payload.message;
                                el.askStage.textContent = payload.message;
                                break;
                            case "sources":
                                renderSources(payload);
                                break;
                            case "token":
                                if (firstTokenAt === null) firstTokenAt = elapsed();
                                tokenCount++;
                                raw += payload.v;
                                el.answer.innerHTML = renderAnswer(raw);
                                break;
                            case "error":
                                el.askError.textContent = payload.message;
                                show(el.askError, true);
                                break;
                            case "done":
                                el.askStage.textContent = "";
                                if (firstTokenAt !== null) {
                                    const total = elapsed();
                                    const rate = tokenCount / Math.max(total - firstTokenAt, 0.001);

                                    lastResult = {
                                        dataset: askedDataset,
                                        scope: el.scopeSelect.value ? askedScope : "The whole dataset",
                                        question: q,
                                        answer: raw,
                                        sources: lastSources,
                                        askedAt: askedAt.toLocaleString(),
                                        stamp: [
                                            askedAt.getFullYear(),
                                            String(askedAt.getMonth() + 1).padStart(2, "0"),
                                            String(askedAt.getDate()).padStart(2, "0"),
                                            "-",
                                            String(askedAt.getHours()).padStart(2, "0"),
                                            String(askedAt.getMinutes()).padStart(2, "0"),
                                            String(askedAt.getSeconds()).padStart(2, "0")
                                        ].join("")
                                    };

                                    el.answer.insertAdjacentHTML("beforeend",
                                        statsRow(tokenCount, firstTokenAt, total, rate));
                                }
                                break;
                        }
                    }
                }
            })
            .catch((e) => {
                if (e.name === "AbortError") return;
                el.askError.textContent = e.message;
                show(el.askError, true);
            })
            .finally(() => {
                clearInterval(ticker);
                if (firstTokenAt === null) el.answer.innerHTML = "";
                el.askBtn.disabled = false;
                show(el.stopBtn, false);
                askController = null;
            });
    }

    function renderSources(list) {
        lastSources = list ?? [];
        if (!list.length) return;

        el.sourcesCount.textContent =
            `${list.length} passage${list.length === 1 ? "" : "s"} · best match ${list[0].score}`;

        el.sources.innerHTML = list.map((s) => `
            <details class="source">
                <summary>
                    <span class="citation">[${s.number}]</span>
                    <strong>${escapeHtml(s.fileName)}</strong>
                    <span class="text-muted">page ${s.pageNumber}</span>
                    <span class="badge bg-light text-muted ms-1">${s.score}</span>
                </summary>
                <div class="source-text">${escapeHtml(s.excerpt)}</div>
                <div class="small text-muted mt-1">${escapeHtml(s.path)}</div>
            </details>`).join("");

        show(el.sourcesWrap, true);
    }

    el.askBtn.addEventListener("click", ask);
    el.stopBtn.addEventListener("click", () => askController?.abort());

    el.question.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) ask();
    });

    // ---------- Resource metrics ----------

    const metricsPanel = $("metricsPanel");
    const metricsBody = $("metricsBody");
    const metricsNote = $("metricsNote");
    const metricsSummary = $("metricsSummary");
    let metricsTimer = null;

    function bar(percent, danger = 85, warn = 65) {
        const colour = percent >= danger ? "bg-danger" : percent >= warn ? "bg-warning" : "bg-success";
        return `<div class="progress metric-bar"><div class="progress-bar ${colour}" style="width:${Math.min(percent, 100)}%"></div></div>`;
    }

    function row(label, value, percent) {
        return `<div class="metric-row">
            <span class="metric-label">${label}</span>
            <span class="metric-value">${value}</span>
            <span class="metric-gauge">${percent === undefined ? "" : bar(percent)}</span>
        </div>`;
    }

    async function refreshMetrics() {
        let m;
        try {
            m = await getJson("Metrics");
        } catch {
            return;
        }

        const parts = [];

        parts.push(`<div class="metric-group">This app</div>`);
        parts.push(row("Memory", `${number(m.processRamMb)} MB`));
        parts.push(row("&nbsp;&nbsp;of which vectors", `${number(m.vectorCacheMb)} MB`));
        parts.push(row("CPU", `${m.cpuPercent}%`, m.cpuPercent));

        parts.push(`<div class="metric-group">Machine</div>`);
        parts.push(row("System RAM",
            `${m.systemRamAvailableGb} GB free of ${m.systemRamTotalGb} GB`,
            m.systemRamUsedPercent));

        for (const g of m.gpus) {
            parts.push(row(escapeHtml(g.name), `${g.usedGb} / ${g.totalGb} GB VRAM`, g.usedPercent));
        }

        parts.push(`<div class="metric-group">Ollama models loaded</div>`);
        if (!m.models.length) {
            parts.push(`<div class="metric-row text-muted">None resident — the first question will load one.</div>`);
        }
        for (const model of m.models) {
            parts.push(row(
                escapeHtml(model.name),
                `${model.vramGb} of ${model.sizeGb} GB on GPU (${model.percentOnGpu}%)`,
                model.percentOnGpu));
        }

        metricsBody.innerHTML = parts.join("");

        metricsSummary.textContent =
            `${number(m.processRamMb)} MB · CPU ${m.cpuPercent}% · ${m.systemRamAvailableGb} GB RAM free`;

        if (m.note) {
            metricsNote.textContent = m.note;
            show(metricsNote, true);
        } else {
            show(metricsNote, false);
        }
    }

    // Only sample while the panel is open — no point launching nvidia-smi every few seconds
    // for a panel nobody is looking at.
    metricsPanel.addEventListener("toggle", () => {
        if (metricsPanel.open) {
            refreshMetrics();
            metricsTimer = setInterval(refreshMetrics, 3000);
        } else {
            clearInterval(metricsTimer);
            metricsTimer = null;
        }
    });

    // ---------- Boot ----------

    // Only the sections marked data-remember keep their open state across reloads. Resources,
    // the folder picker and the indexing panel always start collapsed, so every load opens on the
    // library rather than on whatever was left expanded last time.
    for (const section of document.querySelectorAll("details.section[data-remember]")) {
        const key = `ragOpen:${section.id}`;
        const saved = localStorage.getItem(key);
        if (saved !== null) section.open = saved === "1";
        section.addEventListener("toggle", () => localStorage.setItem(key, section.open ? "1" : "0"));
    }

    currentDataset = localStorage.getItem("ragDataset");
    el.folderInput.value = currentDataset
        ? localStorage.getItem(`ragFolder:${currentDataset}`) ?? ""
        : "";

    // Holds a connection open so the app knows the page is still here and can shut itself down
    // when it isn't. EventSource reconnects by itself, so a restarted server is picked up without
    // a reload. Harmless if the feature is switched off — the server just holds the connection.
    new EventSource("?handler=KeepAlive");

    updateIndexTarget();
    checkHealth();
    loadDatasets().then(() => {
        refreshStatus();
        // An indexing run started before this page was loaded should still show its progress.
        startPolling();
        setTimeout(() => { if (!wasRunning) stopPolling(); }, 1500);
    });
})();
