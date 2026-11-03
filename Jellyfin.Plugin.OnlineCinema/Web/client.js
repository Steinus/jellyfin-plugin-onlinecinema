(function () {
    'use strict';

    if (window.__onlineCinemaV0117) return;
    window.__onlineCinemaV0117 = true;

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
        let data = error?.responseJSON || error?.responseJson || {};
        if (!Object.keys(data).length && typeof error?.responseText === 'string') {
            try { data = JSON.parse(error.responseText); } catch (_) { /* non-JSON */ }
        }
        const message = read(data, 'error', null) || read(data, 'message', null) || error?.message || error?.statusText;
        return String(message || 'Не удалось связаться с Jellyfin/TMDB');
    }

    async function api(route) {
        const client = window.ApiClient;
        if (!client || typeof client.getJSON !== 'function') {
            throw new Error('Jellyfin API пока не доступен.');
        }
        return client.getJSON(client.getUrl('OnlineCinema/' + route));
    }

    function apiPost(route, data) {
        const client = window.ApiClient;
        return client.ajax({
            type: 'POST',
            url: client.getUrl('OnlineCinema/' + route),
            data: JSON.stringify(data),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function isVideoPageOpen() {
        return /(?:^|\/)video(?:\?|$)/i.test(location.hash.replace(/^#\/?/, '')) ||
            !!document.querySelector('#videoOsdPage:not(.hide), .videoOsdPage:not(.hide)');
    }

    async function launchJellyfinPlayer(itemId, zone) {
        const client = window.ApiClient;
        if (!client || !itemId) throw Error('Нет подключения к Jellyfin или ID видео.');

        // Confirm the temporary item is actually visible through the normal
        // Jellyfin API before asking its playback manager to start it.
        let item;
        try {
            item = await client.getItem(client.getCurrentUserId(), itemId);
        } catch (error) {
            throw new Error('Jellyfin не видит созданный видеоэлемент: ' + apiError(error));
        }
        if (!item || String(read(item, 'mediaType', '')).toLowerCase() !== 'video') {
            throw new Error('Jellyfin вернул неверную карточку видео. Проверьте плагин.');
        }

        zone.replaceChildren(dom('p', 'oc-message', 'Передаём видео штатному плееру Jellyfin...'));
        let movedToHome = false;
        let accepted = false;
        const deadline = Date.now() + 10000;

        const tryAction = host => {
            // Jellyfin Web registers the itemAction click handler on
            // .itemsContainer, NOT on document.body. Events must bubble
            // through a connected, initialized library items container.
            const action = dom('button', 'itemAction');
            action.type = 'button';
            action.hidden = true;
            action.dataset.action = 'play';
            action.dataset.id = itemId;
            action.dataset.serverid = client.serverId();
            action.dataset.type = 'Movie';
            action.dataset.mediatype = 'Video';
            action.dataset.isfolder = 'false';
            host.append(action);
            const event = new MouseEvent('click', { bubbles: true, cancelable: true });
            action.dispatchEvent(event);
            action.remove();
            return event.defaultPrevented;
        };

        // Try already-mounted containers first. If Online Cinema's custom
        // route unmounted the home view, switch to Jellyfin's real home so
        // React mounts a container with the native playback handler.
        while (Date.now() < deadline && !accepted) {
            const hosts = Array.from(document.querySelectorAll('.itemsContainer'))
                .filter(node => node.isConnected && !node.closest('#oc-user-view'));
            for (const host of hosts) {
                if (tryAction(host)) { accepted = true; break; }
            }
            if (accepted) break;
            if (!movedToHome) {
                movedToHome = true;
                location.hash = '#/home';
            }
            await new Promise(resolve => setTimeout(resolve, 250));
        }

        if (!accepted) {
            if (movedToHome) location.hash = '#/online-cinema';
            throw new Error('Клиент Jellyfin не обработал команду воспроизведения. Не удалось найти активный обработчик медиатеки.');
        }

        // A handled click is not proof the stream started. Make a failure
        // visible rather than leaving "Открываем..." on screen indefinitely.
        await new Promise(resolve => setTimeout(resolve, 6000));
        if (!isVideoPageOpen()) {
            if (movedToHome) location.hash = '#/online-cinema';
            throw new Error('Jellyfin принял команду, но не открыл видеоплеер. Проверьте запросы PlaybackInfo в журнале Jellyfin.');
        }
    }

    function artworkUrl(value) {
        if (!value) return '';
        if (value.startsWith('OnlineCinema/images/')) {
            return window.ApiClient.getUrl(value);
        }
        return value;
    }

    function listingUrl(config, number) {
        return 'discover?type=' + encodeURIComponent(config.type) +
            '&category=' + encodeURIComponent(config.category) +
            '&page=' + encodeURIComponent(number);
    }

    function createPoster(media) {
        const card = button('oc-card', '', () => openMediaDetails(media));
        card.setAttribute('aria-label', media.title + ' — открыть описание');
        const frame = dom('div', 'oc-poster-frame');
        if (media.posterUrl) {
            const poster = dom('img', 'oc-poster');
            poster.loading = 'lazy';
            poster.alt = media.title;
            poster.src = artworkUrl(media.posterUrl);
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

    async function showCollection(config, number = 1) {
        state.currentCollection = config;
        state.page = 1;
        state.query = '';
        const field = current()?.querySelector('#oc-search-input');
        if (field) field.value = '';
        const { generation, root } = startScreen('collection');
        if (!root) return;
        root.append(button('oc-return', '← Все подборки', showHome));
        root.append(dom('h1', 'oc-page-title', config.icon + '  ' + config.title));
        root.append(dom('p', 'oc-lead', config.subtitle));
        const layout = dom('div', 'oc-wall-layout');
        const grid = dom('div', 'oc-grid oc-wall');
        const alphabet = dom('nav', 'oc-alphabet');
        alphabet.setAttribute('aria-label', 'Алфавит по загруженным названиям');
        layout.append(grid, alphabet);
        root.append(layout);
        const notice = dom('p', 'oc-message', 'Загрузка...');
        root.append(notice);
        const letters = 'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯABCDEFGHIJKLMNOPQRSTUVWXYZ#';
        const buttons = new Map();
        let loaded = 0, nextPage = 1, lastPage = 1, busy = false;
        const seen = new Set();
        const updateAlphabet = () => {
            const indexed = new Map();
            Array.from(grid.children).forEach(card => {
                const letter = card.dataset.letter;
                if (letter && !indexed.has(letter)) indexed.set(letter, card);
            });
            for (const [letter, control] of buttons) {
                control.disabled = !indexed.has(letter);
                control.title = control.disabled ? 'Буква ещё не загружена' : 'Перейти к загруженным названиям';
                control.onclick = () => indexed.get(letter)?.scrollIntoView({behavior:'smooth',block:'center'});
            }
        };
        letters.split('').forEach(letter => {
            const x = button('oc-alpha-letter', letter, () => {});
            x.disabled = true;
            buttons.set(letter, x);alphabet.append(x);
        });
        const loadMore = async () => {
            if (busy || nextPage > lastPage && loaded > 0 || !validGeneration(generation)) return;
            busy = true;
            notice.textContent = 'Подгружаем фильмы и сериалы...';
            try {
                const response = page(await api(listingUrl(config, nextPage)));
                if (!validGeneration(generation)) return;
                lastPage = Math.min(500, response.totalPages);
                for (const media of response.items) {
                    const key = media.type + ':' + media.id;
                    if (seen.has(key)) continue;
                    seen.add(key);
                    const card = createPoster(media);
                    const first = (media.title.trim().charAt(0) || '#').toUpperCase();
                    card.dataset.letter = letters.includes(first) ? first : '#';
                    grid.append(card);loaded++;
                }
                nextPage++;
                updateAlphabet();
                notice.textContent = nextPage > lastPage ? 'Все доступные результаты загружены (' + loaded + ').' : 'Загружено: ' + loaded + ' · прокрутите ниже';
            } catch (error) {
                if (validGeneration(generation)) {
                    notice.replaceChildren(dom('span', '', 'Ошибка загрузки: ' + apiError(error) + ' '),
                        button('oc-button', 'Повторить', loadMore));
                }
            } finally {busy = false;}
        };
        const watch = () => {
            if (!validGeneration(generation)) {current()?.removeEventListener('scroll', watch);return;}
            const view = current();
            if (view.scrollTop + view.clientHeight >= view.scrollHeight - 900) loadMore();
        };
        current().addEventListener('scroll', watch, {passive:true});
        await loadMore();
        // Fill large viewports before a user needs to scroll.
        if (validGeneration(generation)) watch();
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
            let response;
            try {
                response = details(await api('details/' + encodeURIComponent(media.type) + '/' + media.id));
            } catch (error) {
                // The catalog entry already supplies usable TMDB metadata.
                // A separate details request must not prevent opening a card.
                response = { item: media, runtime: 0, genres: [], seasons: [] };
            }
            if (!validGeneration(generation)) return;
            const info = response.item;
            panel.replaceChildren();
            const header = dom('div', 'oc-details-header');
            const poster = dom('div', 'oc-detail-poster');
            if (info.posterUrl) {
                const image = dom('img');
                image.src = artworkUrl(info.posterUrl);
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
            if (!info.localItemId) {
                const findReleases = button('oc-primary-button', 'Повторить поиск торрентов', async () => {
                    const resultPanel = dom('div', 'oc-releases');
                    text.querySelector('.oc-releases')?.remove();
                    text.append(resultPanel);
                    setWorking(resultPanel, 'Поиск раздач...');
                    try {
                        const route = 'JacRed/search?title=' + encodeURIComponent(info.title) +
                            '&original=' + encodeURIComponent(info.originalTitle) +
                            '&year=' + encodeURIComponent(info.year) +
                            '&series=' + (info.type === 'tv');
                        const result = await api(route);
                        if (!validGeneration(generation)) return;
                        resultPanel.replaceChildren();
                        const releases = Array.isArray(result) ? result : [];
                        if (!releases.length) {
                            resultPanel.append(dom('p', 'oc-message', 'Источник поиска не нашёл подходящих раздач.'));
                        }
                        releases.forEach(raw => {
                            const title = String(read(raw, 'title', 'Раздача'));
                            const tracker = String(read(raw, 'indexer', ''));
                            const size = Number(read(raw, 'size', 0));
                            const seeds = Number(read(raw, 'seeders', 0));
                            const row = dom('div', 'oc-release-row');
                            row.append(dom('strong', '', title));
                            row.append(dom('span', 'oc-detail-meta',
                                tracker + ' · ' + (size > 0 ? (size / (1024 ** 3)).toFixed(2) + ' ГБ · ' : '') + 'Сиды: ' + seeds));
                            if (read(raw, 'playable', false)) {
                                row.append(button('oc-primary-button', 'Выбрать раздачу', async () => {
                                    const zone = dom('div', 'oc-file-picker');
                                    resultPanel.querySelector('.oc-file-picker')?.remove();
                                    row.after(zone);
                                    zone.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                                    setWorking(zone, 'Подключение к TorrServer...');
                                    try {
                                        const initial = await apiPost('TorrServer/prepare', { key: read(raw,'key','') });
                                        const session = read(initial, 'session', '');
                                        if (!session) throw Error('TorrServer не вернул сеанс.');
                                        const showFiles = async () => {
                                            if (!validGeneration(generation)) return;
                                            const current = await api('TorrServer/files/' + encodeURIComponent(session));
                                            const files = read(current, 'files', []);
                                            zone.replaceChildren();
                                            if (!files.length) {
                                                zone.append(dom('p','oc-message','Ожидание метаданных торрента...'));
                                                zone.append(button('oc-button', 'Проверить ещё раз', showFiles));
                                                return;
                                            }
                                            zone.append(dom('h3','','Выберите видеофайл'));
                                            files.filter(file => /\.(mp4|m4v|mkv|webm|avi|ts)$/i.test(read(file,'path',''))).forEach(file => {
                                                const label = read(file,'path','');
                                                const index = Number(read(file,'id',0));
                                                zone.append(button('oc-file-button', label, async () => {
                                                    setWorking(zone, 'Подготавливаем воспроизведение...');
                                                    try {
                                                        const info = await apiPost('TorrServer/select', {session, fileIndex:index});
                                                        const itemId = read(info, 'itemId', '');
                                                        if (!itemId) throw Error('Jellyfin не вернул ID видео.');
                                                        await launchJellyfinPlayer(itemId, zone);
                                                    } catch (error) {showError(zone,apiError(error),null);}
                                                }));
                                            });
                                        };
                                        await showFiles();
                                    } catch (error) {
                                        showError(zone, 'TorrServer: ' + apiError(error), null);
                                        zone.append(button('oc-button', 'Проверить соединение с TorrServer', async () => {
                                            const diagnostic = dom('p', 'oc-message', 'Проверяем подключение...');
                                            zone.append(diagnostic);
                                            try {
                                                const test = await api('TorrServer/diagnose');
                                                const address = String(read(test, 'host', '')) + ':' + String(read(test, 'port', ''));
                                                diagnostic.textContent = (read(test, 'connected', false) ? 'Соединение есть. ' : 'Нет соединения. ') +
                                                    address + ' — ' + String(read(test, 'error', read(test, 'message', 'Нет ответа')));
                                            } catch (e) {
                                                diagnostic.textContent = 'Не удалось выполнить диагностику: ' + apiError(e);
                                            }
                                        }));
                                    }
                                }));
                            } else {
                                row.append(dom('span', 'oc-note', 'Нет magnet-ссылки — запуск пока недоступен'));
                            }
                            resultPanel.append(row);
                        });
                    } catch (error) {
                        if (validGeneration(generation)) showError(resultPanel, 'Ошибка источника поиска: ' + apiError(error), null);
                    }
                });
                text.append(findReleases);
                // Manual search only: never query JacRed/Prowlarr on card open.
            }
            text.append(dom('p', 'oc-note', 'Для воспроизведения выберите magnet-раздачу и видеофайл.'));
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
                                    still.src = artworkUrl(episode.stillUrl);
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


    const nativeDetailsCache = new Map();
    const knownOnlineNativeIds = new Set();
    let pendingNativeDetails = '';
    let currentNativeId = '';

    function normalizeGuid(value) {
        return String(value || '').replace(/-/g, '').toLowerCase();
    }

    function isCurrentNative(nativeId) {
        return normalizeGuid(currentNativeId) === normalizeGuid(nativeId) &&
            normalizeGuid(getNativeIdFromHash()) === normalizeGuid(nativeId);
    }

    function jellyfinDetails(id) {
        close();
        const serverId = window.ApiClient.serverId();
        location.hash = '#/details?id=' + encodeURIComponent(id) +
            '&serverId=' + encodeURIComponent(serverId);
    }

    async function openMediaDetails(media) {
        if (media.localItemId) {
            jellyfinDetails(media.localItemId);
            return;
        }
        const shown = current();
        const status = shown?.querySelector('#oc-user-content');
        if (status) setWorking(status, 'Открываем карточку Jellyfin...');
        try {
            const key = encodeURIComponent(media.type) + '/' + encodeURIComponent(media.id);
            // The standard catalog API is GET-based and works in Jellyfin's
            // Android WebView too. Keep the former POST path for compatibility.
            const params = new URLSearchParams({
                title: media.title || '',
                original: media.originalTitle || '',
                year: media.year || '',
                poster: media.posterUrl || '',
                backdrop: media.backdropUrl || ''
            });
            let registered;
            try {
                registered = await api('open/' + key + '?' + params.toString());
            } catch (getError) {
                try {
                    registered = await apiPost('Native/open/' + key, {});
                } catch (postError) {
                    throw new Error('GET: ' + apiError(getError) + '; POST: ' + apiError(postError));
                }
            }
            const localId = read(registered, 'localItemId', null);
            const id = localId || read(registered, 'itemId', '');
            if (!id) throw Error('Не удалось зарегистрировать карточку Jellyfin');
            if (!localId) {
                knownOnlineNativeIds.add(normalizeGuid(id));
                // The catalog already supplied these metadata fields, so
                // mobile controls can mount without another API round-trip.
                nativeDetailsCache.set(id, {
                    nativeId: id, type: media.type, tmdbId: media.id,
                    title: media.title, originalTitle: media.originalTitle, year: media.year,
                    selectedVideoId: null
                });
            }
            jellyfinDetails(id);
        } catch (error) {
            // Some embedded Jellyfin clients do not expose the same virtual
            // item endpoints as desktop web. Never strand them on a 404.
            console.warn('Online Cinema: native detail unavailable; using catalog fallback', apiError(error));
            await showDetails(media);
            const panel = current()?.querySelector('#oc-user-content');
            if (panel) panel.prepend(dom('p', 'oc-detail-fallback',
                'Открыта упрощённая карточка: штатная карточка Jellyfin не ответила.'));
        }
    }

    function getNativeIdFromHash() {
        // Normalize desktop #/details and Android #!/details hashes without
        // accidentally generating #//details.
        const hash = location.hash.startsWith('#!') ?
            '#' + location.hash.slice(2) : location.hash;
        const path = hash.replace(/^#/, '');
        if (path !== '/details' && !path.startsWith('/details?') &&
            !path.startsWith('/details/')) return '';
        let id = '';
        try {
            const query = path.includes('?') ? path.slice(path.indexOf('?') + 1) : '';
            id = new URLSearchParams(query).get('id') || '';
            if (!id && path.startsWith('/details/'))
                id = path.slice('/details/'.length).split('?')[0].split('/')[0];
        } catch (_) { return ''; }
        return /^(?:[0-9a-f]{32}|[0-9a-f-]{36})$/i.test(id) ? id : '';
    }

    function torrentQuality(title) {
        const name = String(title || '').toLowerCase();
        const fourK = /(?:^|[\s._()[\]\/-])(?:2160p?|4k|uhd)(?:$|[\s._()[\]\/-])/i.test(name);
        const hd720 = /(?:^|[\s._()[\]\/-])720p?(?:$|[\s._()[\]\/-])/i.test(name);
        const fullHD = /(?:^|[\s._()[\]\/-])1080[pi]?(?:$|[\s._()[\]\/-])/i.test(name);
        const dv = /dolby[\s._-]*vision|dovi|(?:^|[\s._()[\]\/-])dv(?:$|[\s._()[\]\/-])/i.test(name);
        const hdr = /(?:^|[\s._()[\]\/-])(?:hdr|hdr10\+?|hlg)(?:$|[\s._()[\]\/-])/i.test(name);
        return { resolution: fourK ? '2160p' : fullHD ? '1080p' : hd720 ? '720p' : 'other',
            fourK, hd720, hdr, dv };
    }

    function defaultCinemaOptions() {
        return { autoPlayMode: 'auto', autoPlayQuality: '1080p', autoPlaySort: 'quality',
            autoPlayMaxAttempts: 3, autoPlayMinSeeders: 0,
            exclude4K: false, exclude720p: false, excludeHdr: false, excludeDolbyVision: false };
    }

    function releaseMatches(raw, options, resolution = 'all') {
        const q = torrentQuality(read(raw, 'title', ''));
        if (options.exclude4K && q.fourK) return false;
        if (options.exclude720p && q.hd720) return false;
        if (options.excludeHdr && q.hdr) return false;
        if (options.excludeDolbyVision && q.dv) return false;
        if (Number(read(raw, 'seeders', 0)) < Number(options.autoPlayMinSeeders || 0)) return false;
        return resolution === 'all' || q.resolution === resolution;
    }

    function qualityScore(raw, preferred) {
        const resolution = torrentQuality(read(raw, 'title', '')).resolution;
        const baseline = { '2160p': 3, '1080p': 2, '720p': 1, other: 0 };
        if (preferred === 'any') return baseline[resolution] || 0;
        if (preferred === resolution) return 10;
        if (preferred === '1080p') return resolution === '720p' ? 7 : resolution === 'other' ? 4 : 2;
        if (preferred === '2160p') return resolution === '1080p' ? 7 : resolution === '720p' ? 5 : 2;
        return resolution === '1080p' ? 7 : resolution === 'other' ? 4 : 2;
    }

    function sortReleases(list, options, order) {
        const mode = order || options.autoPlaySort || 'quality';
        const preferred = options.autoPlayQuality || '1080p';
        return [...list].sort((a, b) => {
            const size = Number(read(b, 'size', 0)) - Number(read(a, 'size', 0));
            const seeds = Number(read(b, 'seeders', 0)) - Number(read(a, 'seeders', 0));
            const quality = qualityScore(b, preferred) - qualityScore(a, preferred);
            if (mode === 'size_asc') return -size || seeds;
            if (mode === 'size_desc') return size || seeds;
            if (mode === 'seeders') return seeds || quality || size;
            return quality || seeds || size;
        });
    }

    function addNativeDetailsControls(nativeId, info) {
        const page = document.querySelector('.itemDetailPage, #itemDetailPage');
        // The detailRibbon wrapper can be different in embedded mobile
        // layouts; the standard play button row is a more stable anchor.
        const ribbon = page?.querySelector('.detailRibbon') ||
            page?.querySelector('.mainDetailButtons') ||
            page?.querySelector('.detailPagePrimaryContainer');
        if (!ribbon || !isCurrentNative(nativeId)) return false;
        const old = page.querySelector('#oc-native-controls');
        if (old?.dataset.nativeId === nativeId) return true;
        old?.remove();
        page.classList.add('oc-native-detail');

        const actions = dom('div', 'oc-native-actions');
        actions.id = 'oc-native-controls';
        actions.dataset.nativeId = nativeId;
        const messageBox = dom('div', 'oc-native-play-status');
        const host = dom('div', 'oc-native-results');
        host.hidden = true;
        let loaded = null;
        let searchPromise = null;
        let listSort = 'quality';
        let resolutionFilter = 'all';
        let opts = defaultCinemaOptions();
        const filterState = { ...opts };

        async function searchReleases(force = false) {
            if (!force && Array.isArray(loaded)) return loaded;
            if (searchPromise) return searchPromise;
            const request = 'JacRed/search?title=' + encodeURIComponent(info.title) +
                '&original=' + encodeURIComponent(info.originalTitle) +
                '&year=' + encodeURIComponent(info.year) +
                '&series=' + (info.type === 'tv');
            searchPromise = api(request).then(data => {
                if (!Array.isArray(data)) throw Error('Источник торрентов вернул неожиданный ответ.');
                loaded = data;
                return loaded;
            }).finally(() => { searchPromise = null; });
            return searchPromise;
        }

        async function prepare(raw, statusBox = null, automatic = false) {
            const describe = text => {
                if (statusBox?.isConnected) statusBox.textContent = text;
            };
            const chosen = String(read(raw, 'title', 'Раздача'));
            let started;
            try {
                describe('Добавляем раздачу в TorrServer: ' + chosen);
                started = await apiPost('TorrServer/prepare', { key: read(raw, 'key', '') });
            } catch (error) {
                throw Error('TorrServer не принял magnet: ' + apiError(error));
            }
            let session = String(read(started, 'session', ''));
            if (!session) throw Error('TorrServer не вернул идентификатор сеанса.');

            let files = read(started, 'files', []);
            if (!Array.isArray(files)) files = [];
            let lastStatus = String(read(started, 'status', ''));
            let lastError = '';
            const checks = automatic ? 18 : 30;
            for (let attempt = 0; !files.length && attempt < checks; attempt++) {
                if (!isCurrentNative(nativeId)) throw Error('Подготовка отменена: карточка закрыта.');
                if (attempt > 0) await new Promise(resolve => setTimeout(resolve, 1500));
                describe('TorrServer загружает метаданные: ' + (attempt + 1) + '/' +
                    checks + (lastStatus ? ' · ' + lastStatus : '') +
                    (lastError ? ' · ' + lastError : ''));
                try {
                    // POST follows the same reliable API path as /prepare;
                    // embedded Android Jellyfin clients may return 404 for GET /files.
                    const response = await apiPost('TorrServer/files', { session });
                    files = read(response, 'files', []);
                    if (!Array.isArray(files)) files = [];
                    lastStatus = String(read(response, 'status', lastStatus));
                    lastError = '';
                } catch (error) {
                    const detail = apiError(error);
                    lastError = detail;
                    if (attempt === checks - 1) {
                        throw Error('Не удалось получить файлы раздачи от Jellyfin/TorrServer: ' + detail);
                    }
                    // A transient 404/502 may occur if TorrServer recycled the
                    // torrent. The server now restores the magnet automatically.
                    // Retry instead of immediately failing the selected release.
                }
            }
            const videoFiles = files.filter(file =>
                /\.(mp4|m4v|mkv|webm|avi|ts)$/i.test(String(read(file, 'path', ''))));
            if (!videoFiles.length) {
                throw Error(files.length
                    ? 'TorrServer вернул файлы, но среди них нет видео.'
                    : 'TorrServer не получил метаданные за время ожидания. ' +
                      (lastStatus ? 'Статус: ' + lastStatus + '. ' : '') +
                      'Попробуй другую раздачу с активными пирами.');
            }
            return { session, files: videoFiles };
        }

        async function selectAndPlay(session, file, statusBox) {
            if (statusBox?.isConnected)
                statusBox.textContent = 'Выбираем файл в TorrServer...';
            let result;
            try {
                result = await apiPost('TorrServer/select', {
                    session, fileIndex: Number(read(file, 'id', 0))
                });
            } catch (error) {
                throw Error('Выбор файла TorrServer/select: ' + apiError(error));
            }
            const itemId = String(read(result, 'itemId', ''));
            if (!itemId) throw Error('Jellyfin не вернул ID подготовленного видео.');
            try {
                // Only used to remember last selection; failure here is not
                // a playback error and must not strand Android clients.
                await apiPost('Native/item/' + encodeURIComponent(nativeId) + '/selected',
                    { videoId: itemId });
            } catch (error) {
                console.warn('Online Cinema: could not save recent selection:', apiError(error));
            }
            if (statusBox?.isConnected)
                statusBox.textContent = 'Открываем видеоплеер Jellyfin...';
            try {
                await launchJellyfinPlayer(itemId, statusBox);
            } catch (error) {
                throw Error('Запуск плеера Jellyfin: ' + apiError(error));
            }
        }

        async function automaticPlayback() {
            const statusBox = messageBox;
            let chosen = null;
            try {
                await optionsReady;
                const mode = opts.autoPlayMode || 'auto';
                if (mode === 'manual') {
                    statusBox.textContent = 'Открываем ручной выбор раздач...';
                    await openTorrentList();
                    return;
                }
                if (mode === 'previous') {
                    const state = await api('Native/item/' + encodeURIComponent(nativeId));
                    const selected = read(state, 'selectedVideoId', null);
                    if (selected) {
                        await launchJellyfinPlayer(selected, statusBox);
                        return;
                    }
                }
                statusBox.textContent = 'Автопоиск раздач...';
                const all = await searchReleases();
                const candidates = sortReleases(all.filter(x =>
                    read(x, 'playable', false) && releaseMatches(x, opts)
                ), opts);
                if (!candidates.length) throw Error('Нет подходящих magnet-раздач с учётом фильтров. Измени настройки или нажми «Торренты».');
                const maxAttempts = Math.max(1, Math.min(5, Number(opts.autoPlayMaxAttempts) || 3));
                const failures = [];
                for (let i = 0; i < Math.min(maxAttempts, candidates.length); i++) {
                    chosen = candidates[i];
                    statusBox.textContent = 'Проверяем раздачу ' + (i + 1) + '/' +
                        Math.min(maxAttempts, candidates.length) + ': ' + read(chosen, 'title', '');
                    try {
                        const prepared = await prepare(chosen, statusBox, true);
                        const videos = [...prepared.files];
                        // For series, select the first episode by filename rather than a bonus video.
                        if (info.type === 'tv') {
                            videos.sort((a, b) => String(read(a, 'path', '')).localeCompare(String(read(b, 'path', '')), undefined, { numeric: true }));
                        } else {
                            videos.sort((a, b) => Number(read(b, 'length', 0)) - Number(read(a, 'length', 0)));
                        }
                        statusBox.textContent = 'Запускаем видео через Jellyfin...';
                        await selectAndPlay(prepared.session, videos[0], statusBox);
                        return;
                    } catch (error) {
                        failures.push(apiError(error));
                    }
                }
                throw Error('Ни одна раздача не запустилась: ' + failures.join('; '));
            } catch (error) {
                showError(statusBox, apiError(error), () => automaticPlayback());
            }
        }

        const play = button('oc-native-play', '▶  Воспроизвести', () => automaticPlayback());
        actions.append(play);
        const torrentToggle = button('oc-primary-button', 'Торренты', () => {
            if (!host.hidden) {
                host.hidden = true;
                torrentToggle.textContent = 'Торренты';
                return;
            }
            openTorrentList();
        });
        actions.append(torrentToggle, messageBox, host);
        ribbon.after(actions);

        function selectField(label, values, selected, onChange) {
            const container = dom('label', 'oc-filter-control');
            container.append(dom('span', '', label));
            const select = dom('select', 'oc-filter-select');
            values.forEach(([value, text]) => {
                const option = dom('option', '', text);
                option.value = value;
                if (selected === value) option.selected = true;
                select.append(option);
            });
            select.addEventListener('change', () => onChange(select.value));
            container.append(select);
            return container;
        }

        function checkField(label, property, refresh) {
            const control = dom('label', 'oc-filter-check');
            const input = dom('input');
            input.type = 'checkbox';
            input.checked = !!filterState[property];
            input.addEventListener('change', () => {
                filterState[property] = input.checked;
                refresh();
            });
            control.append(input, dom('span', '', label));
            return control;
        }

        async function openTorrentList(force = false) {
            await optionsReady;
            host.hidden = false;
            torrentToggle.textContent = 'Скрыть торренты';
            let list = host.querySelector('.oc-releases');
            if (list && !force) return;
            host.replaceChildren();
            const loading = dom('p', 'oc-message', 'Ищем раздачи...');
            host.append(loading);
            try {
                const releases = await searchReleases(force);
                if (!isCurrentNative(nativeId)) return;
                host.replaceChildren();
                const controls = dom('div', 'oc-release-filters');
                const entries = dom('div', 'oc-releases');
                const counter = dom('p', 'oc-release-count');
                const refresh = () => {
                    entries.querySelectorAll('.oc-file-picker').forEach(x => x.remove());
                    entries.replaceChildren();
                    const visible = sortReleases(releases.filter(raw =>
                        releaseMatches(raw, filterState, resolutionFilter)
                    ), { ...opts, ...filterState }, listSort);
                    counter.textContent = 'Раздач: ' + visible.length + ' из ' + releases.length;
                    for (const raw of visible) addRelease(entries, raw);
                    if (!visible.length) entries.append(dom('p', 'oc-message', 'Нет результатов с такими фильтрами.'));
                };
                controls.append(selectField('Сортировка', [
                    ['quality','По качеству'], ['seeders','По сидам'],
                    ['size_asc','Размер: меньше → больше'], ['size_desc','Размер: больше → меньше']
                ], listSort, next => { listSort = next; refresh(); }));
                controls.append(selectField('Разрешение', [
                    ['all','Все'], ['2160p','4K / 2160p'], ['1080p','1080p'],
                    ['720p','720p'], ['other','Другое / неизвестно']
                ], resolutionFilter, next => { resolutionFilter = next; refresh(); }));
                const checks = dom('div', 'oc-quality-checks');
                [['Без 4K','exclude4K'], ['Без 720p','exclude720p'],
                    ['Без HDR','excludeHdr'], ['Без Dolby Vision','excludeDolbyVision']]
                    .forEach(([label, key]) => checks.append(checkField(label, key, refresh)));
                const refreshButton = button('oc-button', 'Обновить поиск', () => openTorrentList(true));
                controls.append(checks, refreshButton);
                host.append(controls, counter, entries);
                refresh();
            } catch (error) {
                showError(host, 'Ошибка поиска: ' + apiError(error), () => openTorrentList(true));
            }
        }

        function addRelease(list, raw) {
            const row = dom('div', 'oc-release-row');
            const title = String(read(raw, 'title', 'Раздача'));
            const flags = torrentQuality(title);
            const size = Number(read(raw, 'size', 0));
            const seeds = Number(read(raw, 'seeders', 0));
            row.append(dom('strong', '', title));
            const details = dom('div', 'oc-detail-meta',
                String(read(raw, 'indexer', '')) + ' · ' +
                (size > 0 ? (size / (1024 ** 3)).toFixed(2) + ' ГБ · ' : '') +
                'Сиды: ' + seeds);
            row.append(details);
            const tags = dom('div', 'oc-release-tags');
            const badges = [flags.resolution !== 'other' ? flags.resolution : null,
                flags.hdr ? 'HDR' : null, flags.dv ? 'Dolby Vision' : null].filter(Boolean);
            badges.forEach(x => tags.append(dom('span', 'oc-release-tag', x)));
            row.append(tags);
            if (!read(raw, 'playable', false)) {
                row.append(dom('p', 'oc-note', 'Нет magnet-ссылки: запуск недоступен'));
            } else {
                row.append(button('oc-primary-button', 'Выбрать раздачу', async () => {
                    list.querySelector('.oc-file-picker')?.remove();
                    const zone = dom('div', 'oc-file-picker');
                    row.after(zone);
                    zone.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    setWorking(zone, 'Подключение к TorrServer...');
                    try {
                        const prepared = await prepare(raw, zone, false);
                        if (!isCurrentNative(nativeId)) return;
                        zone.replaceChildren(dom('h3', '', 'Выбери видеофайл'));
                        for (const file of prepared.files) {
                            const name = String(read(file, 'path', ''));
                            zone.append(button('oc-file-button', name, async () => {
                                setWorking(zone, 'Запускаем видео...');
                                try {
                                    await selectAndPlay(prepared.session, file, zone);
                                } catch (error) {
                                    showError(zone, apiError(error), null);
                                }
                            }));
                        }
                    } catch (error) {
                        showError(zone, apiError(error), null);
                    }
                }));
            }
            list.append(row);
        }

        const optionsReady = api('Native/options').then(settings => {
            opts = {
                ...opts,
                autoPlayMode: String(read(settings, 'autoPlayMode', opts.autoPlayMode)),
                autoPlayQuality: String(read(settings, 'autoPlayQuality', opts.autoPlayQuality)),
                autoPlaySort: String(read(settings, 'autoPlaySort', opts.autoPlaySort)),
                autoPlayMaxAttempts: Number(read(settings, 'autoPlayMaxAttempts', 3)),
                autoPlayMinSeeders: Number(read(settings, 'autoPlayMinSeeders', 0))
            };
            Object.assign(filterState, {
                ...opts,
                exclude4K: !!read(settings, 'exclude4K', false),
                exclude720p: !!read(settings, 'exclude720p', false),
                excludeHdr: !!read(settings, 'excludeHdr', false),
                excludeDolbyVision: !!read(settings, 'excludeDolbyVision', false),
                autoPlayMinSeeders: Number(read(settings, 'autoPlayMinSeeders', 0))
            });
        }).catch(error => {
            console.warn('Online Cinema: playback preferences unavailable:', error);
        });
        return true;
    }

    async function decorateNativeDetails() {
        const nativeId = getNativeIdFromHash();
        if (!nativeId) {
            currentNativeId = '';
            const page = document.querySelector('.itemDetailPage, #itemDetailPage');
            page?.classList.remove('oc-native-detail');
            page?.querySelector('#oc-native-controls')?.remove();
            return;
        }
        currentNativeId = nativeId;
        let info = nativeDetailsCache.get(nativeId);
        if (info === null) return;
        if (!info) {
            if (pendingNativeDetails === nativeId) return;
            pendingNativeDetails = nativeId;
            try {
                info = await api('Native/item/' + encodeURIComponent(nativeId));
                knownOnlineNativeIds.add(normalizeGuid(nativeId));
                nativeDetailsCache.set(nativeId, info);
            } catch (error) {
                // Normal library items do not belong to Online Cinema.
                nativeDetailsCache.set(nativeId, null);
                return;
            } finally {
                if (pendingNativeDetails === nativeId) pendingNativeDetails = '';
            }
        }
        addNativeDetailsControls(nativeId, info);
    }

    function homeEntry() {
        const target = document.querySelector('#homeTab .sections');
        if (!target || target.querySelector('#oc-home-entry')) return;
        const shortcut = button('oc-home-entry', '🎬  Онлайн-кинотеатр', open);
        shortcut.id = 'oc-home-entry';
        target.prepend(shortcut);
    }

    // Android uses the native Play action in the legacy Jellyfin detail
    // template. Capture it *before* the Jellyfin handler so an unprepared
    // catalog item cannot launch an empty, silent player. The restriction to
    // registered online IDs avoids affecting normal library playback.
    function interceptNativeOnlinePlay(event) {
        const target = event.target;
        if (!target || typeof target.closest !== 'function') return;
        const trigger = target.closest(
            '.itemDetailPage .mainDetailButtons [data-action="play"],' +
            '.itemDetailPage .mainDetailButtons [data-action="resume"],' +
            '#itemDetailPage .mainDetailButtons [data-action="play"],' +
            '#itemDetailPage .mainDetailButtons [data-action="resume"]'
        );
        if (!trigger || trigger.closest('#oc-native-controls')) return;
        const id = getNativeIdFromHash();
        if (!id || !knownOnlineNativeIds.has(normalizeGuid(id))) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const start = async () => {
            await decorateNativeDetails();
            let button = document.querySelector('#oc-native-controls .oc-native-play');
            if (!button) {
                // In slower embedded WebViews a native page may be mid-render.
                await new Promise(resolve => setTimeout(resolve, 300));
                await decorateNativeDetails();
                button = document.querySelector('#oc-native-controls .oc-native-play');
            }
            if (button) {
                button.click();
            } else {
                const page = document.querySelector('.itemDetailPage, #itemDetailPage');
                const warning = dom('p', 'oc-error',
                    'Не удалось подключить управление Онлайн-кинотеатра к мобильной карточке. Вернись в каталог и открой фильм повторно.');
                page?.querySelector('.mainDetailButtons')?.after(warning);
            }
        };
        start().catch(error => {
            const page = document.querySelector('.itemDetailPage, #itemDetailPage');
            const status = dom('p', 'oc-error', 'Ошибка онлайн-воспроизведения: ' + apiError(error));
            page?.querySelector('.mainDetailButtons')?.after(status);
        });
    }

    let observerScheduled = false;
    const observer = new MutationObserver(() => {
        if (observerScheduled) return;
        observerScheduled = true;
        requestAnimationFrame(() => {
            observerScheduled = false;
            menu();
            homeEntry();
            decorateNativeDetails();
            const view = current();
            if (view && !view.hidden && !location.hash.startsWith('#/online-cinema')) close();
        });
    });

    function init() {
        if (!document.body) return;
        document.addEventListener('click', interceptNativeOnlinePlay, true);
        observer.observe(document.body, { childList: true, subtree: true });
        menu();
        homeEntry();
        decorateNativeDetails();
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
        decorateNativeDetails();
    });
})();
