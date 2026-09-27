/* Euro 2024 RAG — chat UI (jQuery). Talks to /api/status and /api/chat (NDJSON stream). */
$(function () {
    "use strict";

    const examples = [
        { q: "Who won Euro 2024 and how did they get there?", hint: "Tournament path" },
        { q: "Türkiye turnuvada nasıl bir performans gösterdi?", hint: "Team summary · Turkish" },
        { q: "Which team scored the most goals, and did they over-perform their xG?", hint: "Leaderboards" },
        { q: "İspanya - Almanya çeyrek finalinin istatistiklerini karşılaştır.", hint: "Match report · Turkish" },
        { q: "Show me the Group F standings.", hint: "Group table" },
        { q: "Which knockout matches were decided on penalties?", hint: "Knockout details" },
        { q: "Which stadium had the highest attendance?", hint: "Venues" },
        { q: "Which team received the most yellow cards?", hint: "Discipline" },
    ];

    const $thread = $("#thread");
    const $empty = $("#empty");
    const $input = $("#input");
    const $composer = $("#composer");
    const $send = $("#send");

    /** Conversation sent to the server: [{ role: "user" | "assistant", content }] */
    let history = [];
    let controller = null;
    let ready = false;

    marked.setOptions({ gfm: true, breaks: false });

    // ---------- Examples ----------
    examples.forEach(function (ex, i) {
        const $btn = $("<button type='button'>").text(ex.q).on("click", () => ask(ex.q));
        $("<li>").append($btn).appendTo("#sidebar-suggestions");
        if (i < 4) {
            $("<button type='button'>").text(ex.q).append($("<small>").text(ex.hint))
                .on("click", () => ask(ex.q)).appendTo("#example-grid");
        }
    });

    // ---------- Index status ----------
    // Sending is enabled once the server has finished indexing; until then the placeholder says why.
    const placeholder = $input.attr("placeholder");

    function pollStatus() {
        $.getJSON("/api/status").done(function (s) {
            const ing = s.ingestion;
            ready = ing.status === "Ready";
            $input.attr("placeholder", ready ? placeholder : ing.message);
            updateSendState();
            if (ing.status === "Pending" || ing.status === "Running") {
                setTimeout(pollStatus, 1000);
            }
        }).fail(function () {
            $input.attr("placeholder", "Server unreachable, retrying…");
            setTimeout(pollStatus, 3000);
        });
    }

    // ---------- Composer ----------
    function updateSendState() {
        $send.prop("disabled", !controller && (!ready || !$input.val().trim()));
    }

    function autosize() {
        const height = $input.css("height", "auto")[0].scrollHeight;
        $input.css({ height: Math.min(height, 200) + "px", overflowY: height > 200 ? "auto" : "hidden" });
    }

    $input.on("input", function () { autosize(); updateSendState(); });
    $input.on("keydown", function (e) {
        if (e.key === "Enter" && !e.shiftKey && !e.originalEvent.isComposing) {
            e.preventDefault();
            $composer.trigger("submit");
        }
    });

    $composer.on("submit", function (e) {
        e.preventDefault();
        if (controller) {
            controller.abort();
            return;
        }
        ask($input.val());
    });

    $("#new-chat").on("click", function () {
        if (controller) controller.abort();
        history = [];
        $thread.children(".msg").remove();
        $empty.show();
        $("#sidebar").removeClass("open");
        $input.val("").trigger("focus");
        autosize();
        updateSendState();
    });

    $("#menu-toggle").on("click", () => $("#sidebar").toggleClass("open"));

    // ---------- Chat ----------
    async function ask(text) {
        text = (text || "").trim();
        if (!text || controller || !ready) return;

        $("#sidebar").removeClass("open");
        $empty.hide();
        $input.val("");
        autosize();

        $($("#tpl-user").html()).find(".bubble").text(text).end().appendTo($thread);
        const view = createAssistantView();
        history.push({ role: "user", content: text });

        controller = new AbortController();
        $composer.addClass("busy");
        updateSendState();
        scrollToBottom(true);

        let answer = "";
        try {
            const res = await fetch("/api/chat", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ messages: history }),
                signal: controller.signal,
            });
            if (!res.ok) {
                throw new Error((await res.json().catch(() => null))?.detail || res.statusText);
            }

            await readNdjson(res.body, function (evt) {
                switch (evt.type) {
                    case "query":
                        view.step("Search query", evt.text);
                        break;
                    case "sources":
                        view.sources(evt.data);
                        view.step("Retrieved", evt.data.length + " chunks");
                        break;
                    case "reset":
                        answer = "";
                        view.step("Retry", evt.text);
                        view.reset();
                        break;
                    case "delta":
                        answer += evt.text;
                        view.render(answer);
                        break;
                    case "done":
                        view.done(evt.data);
                        break;
                    case "error":
                        throw new Error(evt.text);
                }
            });

            history.push({ role: "assistant", content: answer });
        } catch (err) {
            if (err.name === "AbortError") {
                view.render(answer || "_Stopped._");
                view.flush();
                if (answer) history.push({ role: "assistant", content: answer });
            } else {
                view.error(err.message, answer);
                history.pop(); // let the user retry the same question
            }
        } finally {
            controller = null;
            $composer.removeClass("busy");
            updateSendState();
            $input.trigger("focus");
        }
    }

    /** Reads a newline-delimited JSON stream and calls onEvent for each line. */
    async function readNdjson(body, onEvent) {
        const reader = body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        for (;;) {
            const { value, done } = await reader.read();
            if (done) break;
            buffer += decoder.decode(value, { stream: true });
            let nl;
            while ((nl = buffer.indexOf("\n")) >= 0) {
                const line = buffer.slice(0, nl).trim();
                buffer = buffer.slice(nl + 1);
                if (line) onEvent(JSON.parse(line));
            }
        }
        if (buffer.trim()) onEvent(JSON.parse(buffer));
    }

    function createAssistantView() {
        const $msg = $($("#tpl-assistant").html()).appendTo($thread);
        const $answer = $msg.find(".answer");
        const $sources = $msg.find(".sources");
        let pending = null;
        let latest = null;

        function flush() {
            if (pending) cancelAnimationFrame(pending);
            pending = null;
            if (latest !== null) {
                $answer.html(toHtml(latest));
                scrollToBottom();
            }
        }

        function toHtml(markdown) {
            const html = DOMPurify.sanitize(marked.parse(markdown));
            // Turn [3] citations into clickable chips that reveal the matching source.
            return html.replace(/\[(\d{1,2})\]/g, '<span class="cite" data-n="$1" title="Show source $1">$1</span>');
        }

        return {
            step: function (label, value) {
                $("<span class='step'>").append(document.createTextNode(label + ": "), $("<b>").text(value))
                    .attr("title", value).appendTo($msg.find(".pipeline"));
            },

            sources: function (items) {
                $sources.prop("hidden", false).find("summary").text("Sources (" + items.length + ")");
                const $list = $sources.find(".source-list");
                items.forEach(function (s) {
                    const pct = Math.max(0, Math.min(100, s.score * 100));
                    $("<li class='source'>").attr("data-n", s.index).append(
                        $("<button type='button' class='source-head'>").append(
                            $("<span class='source-n'>").text(s.index),
                            $("<span class='source-title'>").text(s.title),
                            $("<span class='kind'>").text(s.kind),
                            $("<span class='score'>").append(
                                $("<span class='score-bar'>").append($("<span>").css("width", pct + "%")),
                                document.createTextNode(s.score.toFixed(3)))),
                        $("<div class='source-content'>").text(s.content)
                    ).appendTo($list);
                });
            },

            // Re-render at most once per frame while tokens stream in; the frame draws the latest text.
            render: function (markdown) {
                latest = markdown;
                if (pending) return;
                pending = requestAnimationFrame(flush);
            },

            done: function (info) {
                flush();
                const parts = [];
                parts.push($("<span>").text((info.elapsedMs / 1000).toFixed(1) + "s"));
                if (info.inputTokens != null) {
                    parts.push($("<span>").text(info.inputTokens + " → " + info.outputTokens + " tokens"));
                }
                $msg.find(".meta").append(parts);
            },

            flush: flush,

            reset: function () {
                if (pending) cancelAnimationFrame(pending);
                pending = null;
                latest = null;
                $answer.html('<span class="typing"><i></i><i></i><i></i></span>');
            },

            // Keep whatever was already streamed; the error goes underneath it.
            error: function (message, partialAnswer) {
                if (partialAnswer) {
                    latest = partialAnswer;
                    flush();
                    $("<p class='answer-error'>").text("⚠️ Answer interrupted: " + message).insertAfter($answer);
                } else {
                    $answer.addClass("error").text("⚠️ " + message);
                }
            },
        };
    }

    $thread.on("click", ".cite", function () {
        const $sources = $(this).closest(".msg-body").find(".sources").prop("open", true);
        const $source = $sources.find(".source[data-n='" + $(this).data("n") + "']").addClass("open flash");
        if ($source.length) {
            $source[0].scrollIntoView({ block: "nearest", behavior: "smooth" });
            setTimeout(() => $source.removeClass("flash"), 1200);
        }
    });

    $thread.on("click", ".source-head", function () {
        $(this).closest(".source").toggleClass("open");
    });

    function scrollToBottom(force) {
        const el = $thread[0];
        const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 160;
        if (force || nearBottom) el.scrollTop = el.scrollHeight;
    }

    pollStatus();
    $input.trigger("focus");
});
