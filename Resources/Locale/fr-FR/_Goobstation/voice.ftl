ui-options-hear-self = S'entendre dans le chat vocal
ui-options-voice-chat-volume = Volume du chat vocal
ui-options-function-voice-push-to-talk = Chat vocal : push-to-talk (Local)
ui-options-function-voice-push-to-talk-radio = Chat vocal : push-to-talk (Radio)

ui-escape-voice-chat = Chat vocal

cmd-voicechat-desc = Affiche le guide du chat vocal avec votre lien et code
cmd-voicechat-help = Utilisation : voicechat
cmd-voicechat-unavailable = Le chat vocal n'est pas activé sur ce serveur.

voice-link-connected = Votre micro est connecté.
voice-link-disconnected = Votre micro n'est pas encore connecté.
voice-link-computer-title = Sur cet ordinateur
voice-link-computer-text = Ouvre la page vocale dans votre navigateur.
voice-link-phone-title = Sur votre téléphone
voice-link-phone-text = Allez à cette adresse, puis saisissez le code :
voice-link-expiry = Laissez la page ouverte pendant que vous jouez. Le code ne fonctionne que jusqu'à ce que vous quittiez le serveur.
voice-link-loading = Récupération de votre lien...
voice-link-open = Ouvrir dans le navigateur
voice-link-copy = Copier le lien
voice-link-close = Fermer

voice-guide-title = Chat Vocal
voice-guide-unbound = (aucune touche définie)
voice-guide-intro = Le chat vocal sur ce serveur fonctionne comme une conversation en personne. Votre voix provient de votre personnage, et seules les personnes assez proches pour l'entendre vous entendront.
voice-guide-talking-title = Parler
voice-guide-talking =
    Plus quelqu'un est loin, moins il vous entendra fort. Tout ce qui se trouve entre vous, comme un mur, une porte ou une fenêtre, étouffe votre voix.

    Parlez plus fort que d'habitude et vous crierez, ce qui s'entendra plus loin. Les chuchotements ne peuvent être entendus que par les gens à quelques tuiles.
    Dans toute situation où vous ne pourriez normalement pas taper dans le chat de SS14, comme quand vous êtes muet, mort, etc., vous ne pourrez pas parler à voix haute.

    Maintenez [bold]{$local}[/bold] pour parler. Si vous préférez ne pas utiliser le push-to-talk, passez en activation vocale sur la page du chat vocal.
voice-guide-radio-title = Radio
voice-guide-radio =
    Maintenez [bold]{$radio}[/bold] pour parler par radio. Les personnes à proximité peuvent encore vous entendre comme si vous parliez normalement.

    Le bouton radio dans le chat sert à sélectionner votre canal. S'il est sur Local, [bold]{$radio}[/bold] utilise le premier canal de votre casque. Le même menu vous permet de masquer les canaux qui ne vous intéressent pas et de baisser le volume radio.

    En activation vocale, choisir un canal vous garde sur la radio chaque fois que vous parlez, alors remettez sur Local quand vous avez fini. Les radios portatives et intercoms avec leur micro activé vous capteront aussi si vous parlez près d'eux.
voice-guide-station-title = Dans la station
voice-guide-station =
    Les téléphones et holopads captent les gens à proximité. Les mégaphones permettent d'être entendu de plusieurs tuiles plus loin. Si vous avez l'accès, une console de communications permet de diffuser dans toute la station.

    L'IA entend et parle à travers ses caméras. Les fantômes peuvent se parler entre eux, mais les joueurs vivants ne peuvent pas les entendre.
voice-guide-controls-title = Contrôles
voice-guide-controls =
    Les icônes de micro et d'écouteurs à côté de la boîte de chat coupent votre micro ou vous rendent sourd. Faites un clic droit sur quelqu'un pour le couper, l'augmenter ou le baisser.

    Le volume vocal est dans Options sous Audio. Vous pouvez reconfigurer les touches push-to-talk sous Contrôles.
voice-guide-rules-title = Règles
voice-guide-rules =
    Le chat vocal a les mêmes règles que le chat textuel. Avec deux exceptions :
    1. Gardez votre microphone à un niveau raisonnable. Si vous faites intentionnellement du micspam/earrape, vous serez muté ou banni.
    2. Seul le clown peut utiliser des soundboards.
    Amusez-vous bien !
voice-guide-connect-title = Connecter votre micro

cmd-voicemute-desc = Active/désactive le mute vocal d'un joueur.
cmd-voicemute-help = Utilisation : voicemute <nom d'utilisateur>
cmd-voicemute-hint = <nom d'utilisateur>
cmd-voicemute-muted = Chat vocal muté pour {$player}.
cmd-voicemute-unmuted = Chat vocal démuté pour {$player}.

comms-console-menu-voice-broadcast-button = Diffusion Vocale
comms-console-menu-voice-broadcast-button-tooltip = Parlez à toute la station via le chat vocal pendant un court moment.
comms-console-menu-voice-broadcast-stop = Fin de la diffusion vocale ({$seconds}s)
comms-console-menu-voice-broadcast-busy = Diffusion vocale en cours
comms-console-menu-voice-broadcast-cooldown = Diffusion vocale ({$seconds}s)

voice-broadcast-denied = Accès refusé.
voice-broadcast-not-connected = Ouvrez d'abord le chat vocal : Échap, Chat vocal.
voice-broadcast-started = Vous diffusez à toute la station pendant {$seconds} secondes.
voice-broadcast-ended = Votre diffusion vocale est terminée.

voice-radio-button-off = Local
voice-radio-button-tooltip = Radio du chat vocal : choisissez le canal sur lequel vous parlez, les canaux que vous entendez et le volume radio.

voice-verb-category = Chat vocal
voice-verb-mute = Couper
voice-verb-unmute = Réactiver
voice-verb-volume = Volume {$percent}%

voice-prompt-later = Pas maintenant
voice-prompt-never = Ne plus demander

voice-speaker-unknown = Inconnu
voice-route-radio = Radio
voice-route-telephone = Téléphone
voice-route-holopad = Holopad
voice-route-camera = Caméra
voice-route-broadcast = Diffusion
voice-speaker-self = Vous
voice-self-blocked = Impossible de parler

voice-radio-popup-talk = Parler
voice-radio-popup-hear = Écouter
voice-radio-popup-volume = Volume
voice-radio-popup-volume-value = {$percent}%

voice-loudness-shout = Cri
voice-loudness-whisper = Chuchotement
voice-loudness-megaphone = Mégaphone

admin-player-actions-window-voice-logs = Journaux Vocaux
cmd-voicelogs-desc = Ouvre le visualiseur des journaux du chat vocal.
cmd-voicelogs-help = Utilisation : voicelogs [nom d'utilisateur]
cmd-voicelogs-hint = [nom d'utilisateur]
voice-logs-verb = Journaux Vocaux
voice-logs-title = Journaux Vocaux
voice-logs-round = Manche
voice-logs-round-entry = Manche {$id} · {$length}
voice-logs-round-current = Manche {$id} (actuelle) · {$length}
voice-logs-player = Joueur
voice-logs-add = Ajouter
voice-logs-refresh = Rafraîchir
voice-logs-play = Lire
voice-logs-pause = Pause
voice-logs-fit = Ajuster
voice-logs-loading = Chargement…
voice-logs-empty = Choisissez un joueur et appuyez sur Ajouter pour empiler leur piste vocale ici.
voice-logs-mute = Couper cette piste
voice-logs-solo = Solo cette piste
voice-logs-remove = Retirer cette piste
voice-logs-lobby = Lobby
voice-logs-blocked = {$name} (bloqué)

voice-god-verb = Voix de Dieu
voice-god-verb-stop = Arrêter la Voix de Dieu
voice-god-started = Vous parlez maintenant à {$target} en tant que Voix de Dieu. Personne d'autre ne peut vous entendre.
voice-god-started-offline = Vous parlez maintenant à {$target} en tant que Voix de Dieu, mais votre page vocale n'est pas ouverte. Ouvrez-la depuis Échap > Chat vocal.
voice-god-hear-self = Vous vous’entendrez aussi en tant que voix, avec la même lueur et lumière sur votre écran.
voice-god-stopped = Vous ne parlez plus en tant que Voix de Dieu.
voice-god-name = Une Voix
voice-god-log-name = Voix de Dieu vers {$target}
voice-route-god = Divine
voice-self-god = Voix de Dieu
cmd-voiceofgod-desc = Parlez en tant que Voix de Dieu à un joueur, tous ceux à proximité, un département, ou toute la station. Exécutez sans arguments pour arrêter.
cmd-voiceofgod-help = Utilisation : voiceofgod <nom d'utilisateur> [hearSelf] | voiceofgod radius <tuiles> [hearSelf] | voiceofgod department <id> [hearSelf] | voiceofgod station [hearSelf]
cmd-voiceofgod-hint = <nom d'utilisateur> | radius | department | station
cmd-voiceofgod-radius-hint = <tuiles>
cmd-voiceofgod-department-hint = <département>
cmd-voiceofgod-hear-self-hint = [hearSelf]
cmd-voiceofgod-bad-hear-self = hearSelf doit être true ou false.
cmd-voiceofgod-bad-radius = Donnez un rayon en tuiles entre 1 et {$max}.
cmd-voiceofgod-bad-department = Ce département n'existe pas.
voice-god-target-radius = tous ceux à {$radius} tuiles de vous
voice-god-target-department = le département {$department}
voice-god-target-station = toute la station
voice-god-cue = Une voix résonne dans votre tête...

voice-mic-offline = Le chat vocal n'est pas ouvert. Cliquez pour l'ouvrir.
voice-mic-ready = Microphone activé. Cliquez pour couper.
voice-mic-live = Vous êtes en direct. Cliquez pour couper.
voice-mic-blocked = Votre personnage ne peut pas parler pour le moment.
voice-mic-muted = Microphone coupé. Cliquez pour réactiver.
voice-mic-deafened = Sourd. Cliquez pour rétablir l'audio et réactiver le micro.
voice-deafen-off = Rendre sourd : silencie tout le chat vocal et coupe votre microphone.
voice-deafen-on = Sourd. Cliquez pour rétablir l'audio.
voice-radio-popup-mute-all = Couper tout
voice-radio-popup-unmute-all = Réactiver tout