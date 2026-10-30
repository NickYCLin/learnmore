// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// 全域輔助函數
function escapeHtml(text) {
    if (!text) return '';
    const map = {
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
    };
    return String(text).replace(/[&<>"']/g, m => map[m]);
}

// 輸出到全域
if (typeof window !== 'undefined') {
    window.escapeHtml = escapeHtml;
}

(() => {
    if (typeof document === 'undefined') return;

    function hidePublicReviewNotice() {
        const notice = document.querySelector('.lyrics-processing-alert');
        if (!notice || notice.querySelector('.badge')?.textContent.trim() !== '需要人工校正') return;

        const lyrics = Array.from(document.querySelectorAll('.lyrics-line[data-lyric-id][data-timestamp]'));
        if (lyrics.length === 0) return;
        if (lyrics.some(line => {
            const chinese = line.querySelector('.cn-line')?.textContent.trim();
            return !chinese || chinese === '翻譯中...';
        })) return;

        notice.remove();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', hidePublicReviewNotice, { once: true });
    } else {
        hidePublicReviewNotice();
    }
})();
