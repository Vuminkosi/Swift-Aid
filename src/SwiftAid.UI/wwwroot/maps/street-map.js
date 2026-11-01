const maps = new WeakMap();
let libraries;
const asset = path => new URL(path, import.meta.url).href;
function style(path) {
    const href = asset(path);
    if (document.querySelector(`link[href="${href}"]`)) return;
    const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = href; document.head.append(link);
}
function script(path) {
    return new Promise((resolve, reject) => {
        const element = document.createElement('script'); element.src = asset(path);
        element.onload = resolve; element.onerror = reject; document.head.append(element);
    });
}
async function load() {
    libraries ??= (async () => {
        style('../vendor/leaflet/leaflet.css'); style('../vendor/leaflet/MarkerCluster.css');
        await script('../vendor/leaflet/leaflet.js');
        await script('../vendor/leaflet/leaflet.markercluster.js');
    })();
    return libraries;
}
function rings(feature) {
    return feature.paths.flatMap(path => path.split('M').filter(Boolean).map(ring => {
        const numbers = ring.match(/-?\d+(?:\.\d+)?/g).map(Number);
        const points = [];
        for (let index = 0; index < numbers.length; index += 2)
            points.push([90 - numbers[index + 1] * 180 / 480, numbers[index] * 360 / 960 - 180]);
        return points;
    }));
}
function badge(count, animal = false) {
    const label = document.createElement('span'); label.textContent = count.toLocaleString();
    return L.divIcon({ html: label, className: `sa-map-marker ${animal ? 'sa-map-marker--animal' : ''}`, iconSize: [36, 36], iconAnchor: [18, 18] });
}
export async function initialize(element, receiver) {
    await load();
    if (maps.has(element)) return;
    const map = L.map(element, { zoomControl: true, scrollWheelZoom: true, touchZoom: true, keyboard: true, minZoom: 2, maxZoom: 18 });
    const geometry = await (await fetch(asset('./natural-earth-countries-v1.json'), { cache: 'force-cache' })).json();
    const data = { map, geometry, receiver, markers: null, outline: null, country: null, focus: null, focusKey: null, initialZoom: 1 };
    maps.set(element, data);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors · Places: <a href="https://www.geonames.org/">GeoNames</a>',
        referrerPolicy: 'strict-origin-when-cross-origin', updateWhenIdle: true, keepBuffer: 1
    }).on('tileerror', () => receiver.invokeMethodAsync('TilesUnavailable').catch(() => {})).addTo(map);
    map.on('zoomend', () => receiver.invokeMethodAsync('ZoomChanged', Math.pow(2, map.getZoom() - data.initialZoom)).catch(() => {}));
    const resize = new ResizeObserver(() => map.invalidateSize({ pan: false })); resize.observe(element); data.resize = resize;
    element.addEventListener('keydown', event => { if (event.key === 'Home') { event.preventDefault(); reset(element); } });
}
export function update(element, country, points, showHuman, showAnimal, focus) {
    const data = maps.get(element); if (!data) return;
    const { map } = data;
    const feature = data.geometry.features.find(item => item.code === country);
    if (data.country !== country && feature) {
        data.country = country;
        if (data.outline) map.removeLayer(data.outline);
        data.outline = L.polygon(rings(feature), { color: '#00865a', weight: 1.5, fillOpacity: .025, interactive: false }).addTo(map);
        data.bounds = data.outline.getBounds();
        map.fitBounds(data.bounds, { padding: [20, 20], animate: false });
        data.initialZoom = map.getZoom();
        data.receiver.invokeMethodAsync('ZoomChanged', 1).catch(() => {});
    }
    if (!feature && !data.country) map.setView([-26.5, 31.5], 7);
    if (data.markers) map.removeLayer(data.markers);
    data.markers = L.markerClusterGroup({ showCoverageOnHover: false, maxClusterRadius: 45,
        iconCreateFunction: cluster => badge(cluster.getAllChildMarkers().reduce((total, marker) => total + (marker.options.reportCount || 0), 0)) });
    for (const point of points || []) {
        const total = (showHuman ? point.humanReports : 0) + (showAnimal ? point.animalReports : 0);
        if (!total || !Number.isFinite(point.latitude) || !Number.isFinite(point.longitude)) continue;
        const popup = document.createElement('div'); popup.className = 'sa-map-popup';
        const title = document.createElement('strong'); title.textContent = point.label; popup.append(title);
        const count = document.createElement('p'); count.textContent = `${total.toLocaleString()} reported cases`; popup.append(count);
        const note = document.createElement('small'); note.textContent = 'Grouped at a public place reference; not individual case locations.'; popup.append(note);
        const marker = L.marker([point.latitude, point.longitude], { icon: badge(total, !showHuman && showAnimal),
            title: `${point.label}: ${total} reports`, reportCount: total, keyboard: true });
        marker.bindPopup(popup); data.markers.addLayer(marker);
    }
    data.markers.addTo(map);
    if (data.focus) { map.removeLayer(data.focus); data.focus = null; }
    if (focus && Number.isFinite(focus.latitude) && Number.isFinite(focus.longitude)) {
        data.focus = L.circleMarker([focus.latitude, focus.longitude], { radius: 8, color: '#6d28d9', fillOpacity: .7 }).addTo(map);
        const label = document.createElement('span'); label.textContent = focus.label + ' — public place reference';
        data.focus.bindPopup(label).openPopup();
        const focusKey = `${country}:${focus.latitude}:${focus.longitude}`;
        if (data.focusKey !== focusKey) map.setView([focus.latitude, focus.longitude], 11, { animate: false });
        data.focusKey = focusKey;
    }
    else data.focusKey = null;
}
export function reset(element) {
    const data = maps.get(element);
    if (data?.bounds) {
        data.map.fitBounds(data.bounds, { padding: [20, 20], animate: false });
        data.initialZoom = data.map.getZoom();
        data.receiver.invokeMethodAsync('ZoomChanged', 1).catch(() => {});
    }
}
export function dispose(element) {
    const data = maps.get(element); if (!data) return;
    data.resize.disconnect(); data.map.remove(); maps.delete(element);
}
