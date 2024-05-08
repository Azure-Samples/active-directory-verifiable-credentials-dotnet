function drawVerifiedIDCard(callback) {
    fetch('/api/issuer/get-manifest')
        .then(function (response) {
            response.text()
                .catch(error => displayMessage(error))
                .then(function (message) {
                    var manifest = JSON.parse(message);
                    document.getElementById('idCardLogo').src = manifest.display.card.logo.uri;
                    document.getElementById('idCardTitle').innerHTML = manifest.display.card.title;
                    document.getElementById('idCardIssuedBy').innerHTML = manifest.display.card.issuedBy;
                    document.getElementById('idCard').style.backgroundColor = manifest.display.card.backgroundColor;
                    document.getElementById('idCardTitle').style.color = manifest.display.card.textColor;
                    document.getElementById('idCardIssuedBy').style.color = manifest.display.card.textColor;
                    document.getElementById('idCard').style.display = "";
                    if (callback) {
                        callback(message);
                    }
                }).catch(error => { console.log(error); })
        }).catch(error => { console.log(error); })
};
