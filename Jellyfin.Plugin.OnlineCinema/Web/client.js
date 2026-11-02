(function () {
    'use strict';

    if (window.__onlineCinemaV013) return;
    window.__onlineCinemaV013 = true;

    // Jellyfin 12 usually serializes .NET DTO properties using PascalCase.
    // Other clients/configurations can use camelCase. Accept both consistently.
    function read(obj, property, fallback) {
        if (!obj || typeof obj !== 'object') return fallback;
        if (Object.prototype.hasOwnProperty.call(obj, property)) return obj[property];
        const alternative = property.charAt(0).toUpperCase() + property.slice(1);
        return Object.prototype.hasOwnProperty.call(obj, alternative) ? obj[alternative] : fallback;
    }

    function item(raw) {
        return {
            id: Number(read(raw, 'id', 0)) || 0,
            type: String(read(raw, 'type', 'movie') || 'movie').toLowerCase(),
            title: String(read(raw, 'title', '') || ''),
            originalTitle: String(read(raw, 'originalTitle', '') || ''),
            year: String(read(raw, 'year', '') || ''),
            overview: String(read(raw, 'overview', '') || ''),
            posterUrl: String(read(raw, 'posterUrl', '') || ''),
            backdropUrl: String(read(raw, 'backdropUrl', '') || ''),
            rating: Number(read(raw, 'rating', 0)) || 0,
            localItemId: read(raw, 'localItemId', null) || null
        };
    }

    function page(raw) {
        const list = read(raw, 'items', undefined);
        if (!Array.isArray(list)) {
            throw new Error('Неожиданный формат ответа каталога. Проверьте версию плагина.');
        }
        return {
            items: list.map(item).filter(x => x.id > 0 && (x.type === 'movie' || x.type === 'tv')),
            page: Math.max(1, Number(read(raw, 'page', 1)) || 1),
            totalPages: Math.max(1, Number(read(raw, 'totalPages', 1)) || 1)
        };
    }

    function details(raw) {
        const info = read(raw, 'item', undefined);
        if (!info) throw new Error('Сервер не вернул карточку фильма или сериала.');
        const seasons = read(raw, 'seasons', []);
        const genres = read(raw, 'genres', []);
        return {
            item: item(info),
            genres: Array.isArray(genres) ? genres : [],
            seasons: Array.isArray(seasons) ? seasons.map(s => ({
                number: Number(read(s, 'number', 0)) || 0,
                name: String(read(s, 'name', '') || ''),
                episodes: Number(read(s, 'episodes', 0)) || 0,
                posterUrl: String(read(s, 'posterUrl', '') || ''),
                airDate: String(read(s, 'airDate', '') || '')
            })) : [],
            runtime: Number(read(raw, 'runtime', 0)) || 0
        };
    }

    function episodes(raw) {
        const list = read(raw, 'episodes', undefined);
        if (!Array.isArray(list)) throw new Error('Некорректный ответ о сериях.');
        return list.map(e => ({
            number: Number(read(e, 'number', 0)) || 0,
            name: String(read(e, 'name', '') || ''),
            overview: String(read(e, 'overview', '') || ''),
            stillUrl: String(read(e, 'stillUrl', '') || ''),
            airDate: String(read(e, 'airDate', '') || '')
        }));
    }

    // Exposed for the built-in CI regression tests.
    window.OnlineCinemaData = { read, item, page, details, episodes };

    const selections = [
        { key: 'popular-movies', title: 'Популярные фильмы', subtitle: 'Самые популярные фильмы на TMDB', icon: '🎞️', type: 'movie', category: 'popular' },
        { key: 'popular-series', title: 'Популярные сериалы', subtitle: 'Сериалы, которые сейчас смотрят', icon: '📺', type: 'tv', category: 'popular' },
        { key: 'now-playing', title: 'Сейчас в кино', subtitle: 'Фильмы в текущем кинопрокате', icon: '🎬', type: 'movie', category: 'now_playing' },
        { key: 'trending-movies', title: 'В тренде — фильмы', subtitle: 'Популярное за эту неделю', icon: '🔥', type: 'movie', category: 'trending' },
        { key: 'top-movies', title: 'Лучшие фильмы', subtitle: 'Высокие оценки зрителей', icon: '⭐', type: 'movie', category: 'top_rated' },
        { key: 'on-air', title: 'Сериалы в эфире', subtitle: 'Новые серии выходят сейчас', icon: '📡', type: 'tv', category: 'on_the_air' },
        { key: 'top-series', title: 'Лучшие сериалы', subtitle: 'Сериалы с высокими оценками', icon: '🏆', type: 'tv', category: 'top_rated' }
    ];

    const state = {
        screen: 'home',
        generation: 0,
        currentCollection: null,
        query: '',
        searchType: 'multi',
        page: 1,
        lastScreen: null
    };

    const viewId = 'oc-user-view';
    const dom = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const button = (className, label, action) => {
        const node = dom('button', className, label);
        node.type = 'button';
        node.addEventListener('click', action);
        return node;
    };
    const current = () => document.getElementById(viewId);
    const content = () => current()?.querySelector('#oc-user-content');

    function apiError(error) {
        const data = error?.responseJSON || error?.responseJson || {};
        const message = read(data, 'error', null) || read(data, 'message', null) || error?.message;
        return String(message || 'Не удалось связаться с Jellyfin/TMDB');
    }

    async function api(route) {
        const client = window.ApiClient;
        if (!client || typeof client.getJSON !== 'function') {
            throw new Error('Jellyfin API пока не доступен.');
        }
        return client.getJSON(client.getUrl('OnlineCinema/' + route));
    }

    function listingUrl(config, number) {
        return 'discover?type=' + encodeURIComponent(config.type) +
            '&category=' + encodeURIComponent(config.category) +
            '&page=' + encodeURIComponent(number);
    }

    function createPoster(media) {
        const card = button('oc-card', '', () => showDetails(media));
        card.setAttribute('aria-label', media.title + ' — открыть описание');
        const frame = dom('div', 'oc-poster-frame');
        if (media.posterUrl) {
            const poster = dom('img', 'oc-poster');
            poster.loading = 'lazy';
            poster.alt = media.title;
            poster.src = media.posterUrl;
            poster.addEventListener('error', () => {
                poster.removeAttribute('src');
                frame.classList.add('oc-no-poster');
            }, { once: true });
            frame.append(poster);
        } else {
            frame.classList.add('oc-no-poster');
            frame.append(dom('span', 'oc-no-image', '🎬'));
        }
        if (media.rating > 0) frame.append(dom('span', 'oc-rating', '★ ' + media.rating.toFixed(1)));
        card.append(frame, dom('div', 'oc-card-title', media.title || 'Без названия'));
        const foot = dom('div', 'oc-card-footer');
        foot.append(dom('span', '', media.year || '—'));
        foot.append(dom('span', 'oc-kind', media.type === 'tv' ? 'СЕРИАЛ' : 'ФИЛЬМ'));
        card.append(foot);
        card.append(dom('div', media.localItemId ? 'oc-library-tag oc-local-tag' : 'oc-library-tag',
            media.localItemId ? 'В библиотеке' : 'Онлайн'));
        return card;
    }

    function setWorking(container, label) {
        container.replaceChildren(dom('p', 'oc-message', label));
    }

    function showError(container, message, retry) {
        container.replaceChildren();
        const box = dom('div', 'oc-error');
        box.append(dom('p', '', message));
        if (retry) box.append(button('oc-button', 'Повторить', retry));
        container.append(box);
    }

    function shell() {
        let view = current();
        if (!view) {
            view = dom('section', 'oc-user-view');
            view.id = viewId;
            document.body.append(view);
        }
        view.replaceChildren();
        const wrap = dom('div', 'oc-shell');
        const header = dom('header', 'oc-header');
        const back = button('oc-back', '← Jellyfin', () => {
            close();
            location.hash = '#/home';
        });
        const brand = dom('div', 'oc-brand');
        brand.append(dom('span', 'oc-logo', '🎬'));
        brand.append(dom('span', 'oc-brand-name', 'Онлайн-кинотеатр'));
        header.append(back, brand, dom('span', 'oc-source', 'Каталог TMDB'));
        wrap.append(header);

        const searchForm = dom('form', 'oc-search');
        const field = dom('input', 'oc-search-input');
        field.id = 'oc-search-input';
        field.type = 'search';
        field.maxLength = 160;
        field.autocomplete = 'off';
        field.placeholder = 'Найти фильм или сериал, например Аватар';
        field.setAttribute('aria-label', 'Поиск фильмов и сериалов');
        field.value = state.query;
        const submit = dom('button', 'oc-primary-button', 'Найти');
        submit.type = 'submit';
        searchForm.append(field, submit);
        searchForm.addEventListener('submit', event => {
            event.preventDefault();
            const query = field.value.trim();
            if (query) showSearch(query, 'multi', 1);
            else showHome();
        });
        wrap.append(searchForm);

        const menu = dom('nav', 'oc-pills');
        menu.setAttribute('aria-label', 'Подборки фильмов и сериалов');
        menu.append(button('oc-pill oc-main-pill', '⌂  Главная', showHome));
        const quick = [selections[0], selections[1], selections[2], selections[4], selections[6]];
        quick.forEach(config => menu.append(button('oc-pill', config.title, () => showCollection(config, 1))));
        wrap.append(menu);

        const main = dom('main', 'oc-content');
        main.id = 'oc-user-content';
        wrap.append(main);
        view.append(wrap);
        view.hidden = false;
        document.body.classList.add('oc-cinema-open');
        return view;
    }

    function validGeneration(generation) {
        const view = current();
        return !!view && !view.hidden && generation === state.generation;
    }

    function startScreen(screen) {
        state.screen = screen;
        const generation = ++state.generation;
        const root = content();
        if (!root) return { generation, root: null };
        root.replaceChildren();
        return { generation, root };
    }

    function showHome() {
        state.page = 1;
        state.query = '';
        const input = current()?.querySelector('#oc-search-input');
        if (input) input.value = '';
        const { generation, root } = startScreen('home');
        if (!root) return;
        root.append(dom('h1', 'oc-page-title', 'Что посмотреть?'));
        root.append(dom('p', 'oc-lead', 'Выбирайте фильмы и сериалы из каталога TMDB. Полистайте подборки или воспользуйтесь поиском.'));

        // Every section has its own error boundary: one failed TMDB request must
        // not hide the other sections.
        selections.forEach(config => {
            const section = dom('section', 'oc-section');
            const head = dom('div', 'oc-section-head');
            const title = dom('div', 'oc-section-heading');
            title.append(dom('h2', '', config.icon + '  ' + config.title));
            title.append(dom('p', '', config.subtitle));
            head.append(title, button('oc-all', 'Все →', () => showCollection(config, 1)));
            section.append(head);
            const shelf = dom('div', 'oc-shelf');
            const track = dom('div', 'oc-track');
            const previous = button('oc-scroll', '‹', () => track.scrollBy({ left: -track.clientWidth * 0.8, behavior: 'smooth' }));
            const next = button('oc-scroll', '›', () => track.scrollBy({ left: track.clientWidth * 0.8, behavior: 'smooth' }));
            previous.setAttribute('aria-label', 'Пролистать назад: ' + config.title);
            next.setAttribute('aria-label', 'Пролистать вперёд: ' + config.title);
            shelf.append(previous, track, next);
            section.append(shelf);
            root.append(section);
            setWorking(track, 'Загрузка подборки...');
            (async () => {
                try {
                    const response = page(await api(listingUrl(config, 1)));
                    if (!validGeneration(generation)) return;
                    track.replaceChildren();
                    if (!response.items.length) {
                        track.append(dom('p', 'oc-message', 'В этой подборке пока нет фильмов.'));
                        return;
                    }
                    response.items.forEach(media => track.append(createPoster(media)));
                } catch (error) {
                    if (validGeneration(generation)) showError(track, 'Не удалось загрузить «' + config.title + '»: ' + apiError(error),
                        () => showHome());
                }
            })();
        });
    }

    function addPager(root, totalPages, activePage, navigate) {
        const footer = dom('nav', 'oc-pagination');
        footer.setAttribute('aria-label', 'Страницы каталога');
        const previous = button('oc-button', '← Назад', () => navigate(activePage - 1));
        const next = button('oc-button', 'Вперёд →', () => navigate(activePage + 1));
        previous.disabled = activePage <= 1;
        next.disabled = activePage >= totalPages;
        footer.append(previous, dom('span', '', activePage + ' / ' + totalPages), next);
        root.append(footer);
    }

    async function showCollection(config, number) {
        state.currentCollection = config;
        state.page = number;
        state.query = '';
        const input = current()?.querySelector('#oc-search-input');
        if (input) input.value = '';
        const { generation, root } = startScreen('collection');
        if (!root) return;
        root.append(button('oc-return', '← Все подборки', showHome));
        root.append(dom('h1', 'oc-page-title', config.icon + '  ' + config.title));
        root.append(dom('p', 'oc-lead', config.subtitle));
        const grid = dom('div', 'oc-grid');
        root.append(grid);
        setWorking(grid, 'Загрузка фильмов и сериалов...');
        try {
            const response = page(await api(listingUrl(config, number)));
            if (!validGeneration(generation)) return;
            grid.replaceChildren();
            if (!response.items.length) grid.append(dom('p', 'oc-message', 'В этой подборке пока ничего нет.'));
            else response.items.forEach(media => grid.append(createPoster(media)));
            addPager(root, response.totalPages, number, p => showCollection(config, p));
        } catch (error) {
            if (validGeneration(generation)) showError(grid, 'Ошибка загрузки: ' + apiError(error), () => showCollection(config, number));
        }
    }

    async function showSearch(query, type, number) {
        state.query = query;
        state.searchType = type;
        state.page = number;
        const input = current()?.querySelector('#oc-search-input');
        if (input) input.value = query;
        const { generation, root } = startScreen('search');
        if (!root) return;
        root.append(button('oc-return', '← Все подборки', showHome));
        root.append(dom('h1', 'oc-page-title', 'Результаты поиска: «' + query + '»'));
        const filters = dom('div', 'oc-filters');
        [['multi', 'Все'], ['movie', 'Фильмы'], ['tv', 'Сериалы']].forEach(entry => {
            filters.append(button('oc-pill ' + (type === entry[0] ? 'oc-pill-active' : ''), entry[1],
                () => showSearch(query, entry[0], 1)));
        });
        root.append(filters);
        const grid = dom('div', 'oc-grid');
        root.append(grid);
        setWorking(grid, 'Ищем в TMDB...');
        try {
            const route = 'search?query=' + encodeURIComponent(query) +
                '&type=' + encodeURIComponent(type) + '&page=' + number;
            const response = page(await api(route));
            if (!validGeneration(generation)) return;
            grid.replaceChildren();
            if (!response.items.length) {
                grid.append(dom('p', 'oc-message', 'По запросу «' + query + '» ничего не найдено.'));
            } else {
                response.items.forEach(media => grid.append(createPoster(media)));
            }
            addPager(root, response.totalPages, number, p => showSearch(query, type, p));
        } catch (error) {
            if (validGeneration(generation)) showError(grid, 'Ошибка поиска: ' + apiError(error), () => showSearch(query, type, number));
        }
    }

    function resumeScreen(snapshot) {
        if (snapshot.screen === 'search') showSearch(snapshot.query, snapshot.type, snapshot.page);
        else if (snapshot.screen === 'collection') showCollection(snapshot.collection, snapshot.page);
        else showHome();
    }

    async function showDetails(media) {
        const snapshot = {
            screen: state.screen,
            query: state.query,
            type: state.searchType,
            collection: state.currentCollection,
            page: state.page
        };
        const { generation, root } = startScreen('detail');
        if (!root) return;
        root.append(button('oc-return', '← Назад к каталогу', () => resumeScreen(snapshot)));
        const panel = dom('div', 'oc-details');
        root.append(panel);
        setWorking(panel, 'Загрузка описания...');
        try {
            const response = details(await api('details/' + encodeURIComponent(media.type) + '/' + media.id));
            if (!validGeneration(generation)) return;
            const info = response.item;
            panel.replaceChildren();
            const header = dom('div', 'oc-details-header');
            const poster = dom('div', 'oc-detail-poster');
            if (info.posterUrl) {
                const image = dom('img');
                image.src = info.posterUrl;
                image.alt = info.title;
                poster.append(image);
            }
            const text = dom('article', 'oc-detail-text');
            text.append(dom('h1', '', info.title + (info.year ? ' (' + info.year + ')' : '')));
            text.append(dom('p', 'oc-detail-meta',
                (info.type === 'tv' ? 'Сериал' : 'Фильм') +
                (info.rating > 0 ? '  ·  ★ ' + info.rating.toFixed(1) : '') +
                (response.runtime ? '  ·  ' + response.runtime + ' мин.' : '')));
            text.append(dom('p', '', info.overview || 'Описание пока отсутствует.'));
            if (response.genres.length) text.append(dom('p', 'oc-detail-meta', response.genres.join(' · ')));
            text.append(dom('p', info.localItemId ? 'oc-library-tag oc-local-tag' : 'oc-library-tag',
                info.localItemId ? 'В библиотеке' : 'Онлайн'));
            if (info.localItemId) {
                text.append(button('oc-primary-button', 'Открыть в медиатеке', () => {
                    close();
                    location.hash = '#!/details?id=' + encodeURIComponent(info.localItemId);
                }));
            }
            text.append(dom('p', 'oc-note', 'Выбор торрент-раздач и запуск через TorrServer добавим на следующем этапе.'));
            header.append(poster, text);
            panel.append(header);
            if (info.type === 'tv' && response.seasons.length) {
                panel.append(dom('h2', 'oc-season-title', 'Сезоны и серии'));
                const chooser = dom('div', 'oc-filters');
                const episodeList = dom('div', 'oc-episodes');
                panel.append(chooser, episodeList);
                response.seasons.forEach(season => {
                    chooser.append(button('oc-pill', season.name || 'Сезон ' + season.number, async () => {
                        chooser.querySelectorAll('button').forEach(b => b.classList.remove('oc-pill-active'));
                        const btns = Array.from(chooser.querySelectorAll('button'));
                        const currentBtn = btns.find(b => b.textContent === (season.name || 'Сезон ' + season.number));
                        if (currentBtn) currentBtn.classList.add('oc-pill-active');
                        setWorking(episodeList, 'Загрузка серий...');
                        try {
                            const list = episodes(await api('seasons/' + info.id + '/' + season.number));
                            if (!validGeneration(generation)) return;
                            episodeList.replaceChildren();
                            list.forEach(episode => {
                                const row = dom('div', 'oc-episode');
                                if (episode.stillUrl) {
                                    const still = dom('img', 'oc-still');
                                    still.src = episode.stillUrl;
                                    still.alt = '';
                                    still.loading = 'lazy';
                                    row.append(still);
                                }
                                const caption = dom('div', 'oc-episode-text');
                                caption.append(dom('strong', '', 'Серия ' + episode.number + ' — ' + episode.name));
                                if (episode.overview) caption.append(dom('p', '', episode.overview));
                                row.append(caption);
                                episodeList.append(row);
                            });
                            if (!list.length) episodeList.append(dom('p', 'oc-message', 'Серии пока не добавлены в TMDB.'));
                        } catch (error) {
                            if (validGeneration(generation)) showError(episodeList, 'Ошибка загрузки серий: ' + apiError(error), null);
                        }
                    }));
                });
            }
        } catch (error) {
            if (validGeneration(generation)) showError(panel, 'Не удалось загрузить карточку: ' + apiError(error),
                () => showDetails(media));
        }
    }

    function open() {
        // Preserve the existing working menu integration; make the catalog
        // visible in the standard Jellyfin user interface, not admin settings.
        if (location.hash !== '#/online-cinema') location.hash = '#/online-cinema';
        shell();
        showHome();
    }

    function close() {
        const view = current();
        if (view) view.hidden = true;
        ++state.generation;
        document.body.classList.remove('oc-cinema-open');
    }

    function menu() {
        const existing = document.getElementById('oc-user-link');
        if (existing && existing.isConnected) return;
        const target = document.querySelector('.MuiDrawer-paper ul[aria-labelledby="libraries-subheader"]')
            || document.querySelector('.MuiDrawer-paper ul')
            || document.querySelector('.mainDrawer-scrollContainer .userMenuOptions,.mainDrawer-scrollContainer');
        if (!target) return;
        const wrapper = dom('li', 'oc-menu-item');
        wrapper.id = 'oc-user-menu-item';
        const link = dom('a', 'oc-menu-link', '🎬  Онлайн-кинотеатр');
        link.id = 'oc-user-link';
        link.href = '#/online-cinema';
        link.addEventListener('click', event => {
            event.preventDefault();
            open();
        });
        wrapper.append(link);
        target.append(wrapper);
    }

    function homeEntry() {
        const target = document.querySelector('#homeTab .sections');
        if (!target || target.querySelector('#oc-home-entry')) return;
        const shortcut = button('oc-home-entry', '🎬  Онлайн-кинотеатр', open);
        shortcut.id = 'oc-home-entry';
        target.prepend(shortcut);
    }

    let observerScheduled = false;
    const observer = new MutationObserver(() => {
        if (observerScheduled) return;
        observerScheduled = true;
        requestAnimationFrame(() => {
            observerScheduled = false;
            menu();
            homeEntry();
            const view = current();
            if (view && !view.hidden && !location.hash.startsWith('#/online-cinema')) close();
        });
    });

    function init() {
        if (!document.body) return;
        observer.observe(document.body, { childList: true, subtree: true });
        menu();
        homeEntry();
        if (location.hash.startsWith('#/online-cinema')) open();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
    window.addEventListener('hashchange', () => {
        if (location.hash.startsWith('#/online-cinema')) {
            const view = current();
            if (!view || view.hidden) open();
        } else close();
    });
})();
