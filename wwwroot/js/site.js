document.addEventListener('DOMContentLoaded', () => {
    const search = document.getElementById('documentSearch');
    const cards = Array.from(document.querySelectorAll('.document-card'));
    const filters = Array.from(document.querySelectorAll('.status-filter'));
    const empty = document.getElementById('emptyState');
    let activeFilter = 'all';

    function applyFilters() {
        const term = (search?.value || '').trim().toLowerCase();
        let visible = 0;
        cards.forEach(card => {
            const matchesText = (card.dataset.search || '').includes(term);
            const matchesStatus = activeFilter === 'all' || card.dataset.status === activeFilter;
            const show = matchesText && matchesStatus;
            card.classList.toggle('d-none', !show);
            if (show) visible++;
        });
        empty?.classList.toggle('d-none', visible !== 0);
    }

    search?.addEventListener('input', applyFilters);
    filters.forEach(button => button.addEventListener('click', () => {
        activeFilter = button.dataset.filter || 'all';
        filters.forEach(item => item.classList.toggle('active', item === button));
        applyFilters();
    }));
});

function showActionNotice(documentName) {
    alert(`Actions for "${documentName}" will be enabled when the document transaction API is connected.`);
}
