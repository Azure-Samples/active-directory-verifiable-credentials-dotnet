async function drawVerifiedIDCard(callback, url = '/api/issuer/get-manifest') {
    try {
        const response = await fetch(url);
        const details = await response.json();
        if (!response.ok) {
            throw new Error(details.error_description || 'Unable to load credential manifest.');
        }
        const card = details.manifest.display.card;
        document.getElementById('idCardLogo').src = card.logo?.uri || '';
        document.getElementById('idCardTitle').textContent = card.title;
        document.getElementById('idCardIssuedBy').textContent = card.issuedBy;
        document.getElementById('idCard').style.backgroundColor = card.backgroundColor;
        document.getElementById('idCardTitle').style.color = card.textColor;
        document.getElementById('idCardIssuedBy').style.color = card.textColor;
        document.getElementById('idCard').style.display = "";
        if (callback) {
            callback(details);
        }
    } catch (error) {
        document.getElementById('message-wrapper').style.display = 'block';
        document.getElementById('message').textContent = 'Credential configuration error: ' + error.message;
    }
};
