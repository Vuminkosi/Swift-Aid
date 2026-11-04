import { selectMedicalCenters, medicalCenterSource } from './medical-centers.js';

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
function badge(count, human, animal) {
    const label = document.createElement('span'); label.textContent = count.toLocaleString();
    const kind = human && animal ? 'mixed' : animal ? 'animal' : 'human';
    return L.divIcon({ html: label, className: `sa-map-marker sa-map-marker--${kind}`, iconSize: [36, 36], iconAnchor: [18, 18] });
}
export async function initialize(element, receiver) {
    await load();
    if (maps.has(element)) return;
    const map = L.map(element, { zoomControl: true, scrollWheelZoom: true, touchZoom: true, keyboard: true, minZoom: 2, maxZoom: 18 });
    const geometry = await (await fetch(asset('./natural-earth-countries-v1.json'), { cache: 'force-cache' })).json();
    let medicalCatalogue = null, medicalUnavailable = false;
    try {
        const response = await fetch(asset('./medical-centers-v1.json'), { cache: 'force-cache' });
        if (!response.ok) throw new Error('Medical-center catalogue unavailable.');
        medicalCatalogue = await response.json();
        if (medicalCatalogue.version !== 1 || !Array.isArray(medicalCatalogue.facilities)) throw new Error('Invalid medical-center catalogue.');
    } catch { medicalUnavailable = true; }
    const data = { map, geometry, receiver, medicalCatalogue, medicalUnavailable, facilities: null, markers: null, outline: null, country: null, focus: null, focusKey: null, initialZoom: 1 };
    maps.set(element, data);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors · Places: <a href="https://www.geonames.org/">GeoNames</a>',
        referrerPolicy: 'strict-origin-when-cross-origin', updateWhenIdle: true, keepBuffer: 1
    }).on('tileerror', () => receiver.invokeMethodAsync('TilesUnavailable').catch(() => {})).addTo(map);
    map.on('zoomend', () => receiver.invokeMethodAsync('ZoomChanged', Math.pow(2, map.getZoom() - data.initialZoom)).catch(() => {}));
    const resize = new ResizeObserver(() => map.invalidateSize({ pan: false })); resize.observe(element); data.resize = resize;
    element.addEventListener('keydown', event => { if (event.key === 'Home') { event.preventDefault(); reset(element); } });
}
export function update(element, country, points, showHuman, showAnimal, focus, showMedicalCenters = true) {
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
        iconCreateFunction: cluster => {
            const markers = cluster.getAllChildMarkers();
            return badge(markers.reduce((total, marker) => total + (marker.options.reportCount || 0), 0),
                markers.some(marker => marker.options.human), markers.some(marker => marker.options.animal));
        } });
    for (const point of points || []) {
        const total = (showHuman ? point.humanReports : 0) + (showAnimal ? point.animalReports : 0);
        if (!total || !Number.isFinite(point.latitude) || !Number.isFinite(point.longitude)) continue;
        const popup = document.createElement('div'); popup.className = 'sa-map-popup';
        const title = document.createElement('strong'); title.textContent = point.label; popup.append(title);
        const count = document.createElement('p'); count.textContent = `${total.toLocaleString()} reported cases`; popup.append(count);
        if (point.cases?.length) {
            const button = document.createElement('button'); button.type = 'button';
            button.className = 'sa-map-popup__cases'; button.textContent = 'View cases >>';
            button.addEventListener('click', async () => {
                button.disabled = true;
                try { await data.receiver.invokeMethodAsync('ViewCases', point.id); }
                catch { button.textContent = 'Try viewing cases again'; }
                finally { button.disabled = false; }
            });
            popup.append(button);
        }
        const note = document.createElement('small'); note.textContent = 'Grouped at a public place reference; not individual case locations.'; popup.append(note);
        const human = showHuman && point.humanReports > 0, animal = showAnimal && point.animalReports > 0;
        const kind = human && animal ? 'Human and animal' : animal ? 'Animal' : 'Human';
        const marker = L.marker([point.latitude, point.longitude], { icon: badge(total, human, animal),
            title: `${point.label}: ${total} reports (${kind})`, reportCount: total, human, animal, keyboard: true });
        marker.bindPopup(popup); data.markers.addLayer(marker);
    }
    data.markers.addTo(map);
    if (data.facilities) { map.removeLayer(data.facilities); data.facilities = null; }
    const facilities = selectMedicalCenters(data.medicalCatalogue, country);
    data.receiver.invokeMethodAsync('MedicalCentersStatus', facilities.length, data.medicalUnavailable).catch(() => {});
    if (showMedicalCenters) {
        data.facilities = L.markerClusterGroup({ showCoverageOnHover: false, maxClusterRadius: 35,
            iconCreateFunction: cluster => {
                const label = document.createElement('span'); label.textContent = `M ${cluster.getChildCount()}`;
                return L.divIcon({ html: label, className: 'sa-medical-marker', iconSize: [40, 28], iconAnchor: [20, 14] });
            } });
        for (const facility of facilities) {
            const label = document.createElement('span'); label.textContent = facility.type === 'Clinic' ? 'C' : 'H';
            const popup = document.createElement('div'); popup.className = 'sa-medical-popup';
            const title = document.createElement('strong'); title.textContent = facility.name; popup.append(title);
            const note = document.createElement('small'); note.textContent = `${facility.type}. Services and opening hours are not verified.`; popup.append(note);
            const source = document.createElement('a'); source.textContent = 'View on OpenStreetMap';
            source.href = medicalCenterSource(facility); source.target = '_blank'; source.rel = 'noopener noreferrer'; popup.append(source);
            L.marker([facility.latitude, facility.longitude], {
                icon: L.divIcon({ html: label, className: 'sa-medical-marker', iconSize: [28, 28], iconAnchor: [14, 14] }),
                title: facility.name + ' — ' + facility.type.toLowerCase(), keyboard: true
            }).bindPopup(popup).addTo(data.facilities);
        }
        data.facilities.addTo(map);
    }
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
