# moon_fps

Projet s&box (FPS / balle-prison). Ouvrir le dossier du projet dans l’éditeur s&box.

## Git

À la racine de ce dossier (`moon_fps`) :

```bash
git init
git add -A
git commit -m "Initial commit"
git branch -M main
git remote add origin https://github.com/TON_COMPTE/moon_fps.git
git push -u origin main
```

Crée d’abord le dépôt vide sur GitHub (sans README) puis remplace l’URL `origin`.

## Collaborateurs — carte « Map » sans WorldSpawn / World Physics

Dans **`MAP_MOON.scene`** et **`Arena.scene`**, le nœud **Map** a **volontairement aucun enfant** dans le fichier `.scene` : la hiérarchie type **WorldSpawn**, monde physique, etc. vient du fichier Hammer **`Assets/maps/map_h.vmap`**, chargé par le composant **MapInstance** (`MapName`).

Si ton pote voit **Map** vide :

1. **`git pull`** à la racine du projet, puis vérifier que ces fichiers existent :  
   `Assets/maps/map_h.vmap`, `map_h.vmap.meta`, et idéalement `map_h.vpk`.
2. Ouvrir le dossier du jeu dans **s&box** (même version que toi), laisser **l’import / compile** des assets finir (barre de progression).
3. Rouvrir la scène **`MAP_MOON.scene`** ou lancer **Play** une fois : certains nœuds n’apparaissent qu’après chargement de la map.
4. Sur la **MapInstance**, vérifier que **`MapName`** est bien `maps/map_h.vmap` (pas d’erreur rouge dans l’inspecteur).

Les **animations** joueur / citizen sont sous `Assets/models/citizen_custom/` et `Assets/Anim/` ; elles sont dans le dépôt. Les fichiers compilés `*.*_c` sont souvent régénérés localement par l’éditeur (voir `.gitignore`) : il faut ouvrir le projet dans s&box au moins une fois pour que tout se compile.
