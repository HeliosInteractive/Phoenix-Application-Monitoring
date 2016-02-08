window.log = function() {
	log.history = log.history || [];
	log.history.push(arguments);
	if(this.console){
		console.log( Array.prototype.slice.call(arguments) );
	}
};

function BaseChannelPath() {
	var base_channel = "/helios/phoenix"
	return base_channel;
}

function SubChannelPath(path) {
	return BaseChannelPath() + "/" + path;
}

function AuthorizeMachine(info){
	$.post("/machines/add", info, function(data) {
		window.location.reload();
	});
}

function AddMachineEntry(payload, columns) {
	try { var msg = $.parseJSON(payload); }
	catch(e) { log("Failed to parse: " + payload); return; }
	
	var row = $('<tr>');
	columns.forEach(function(col) {
		if (!MachineAuthorized(msg)) {
			if (col == "actions") {
				row.append($('<td>').append(
					$('<a>')
					.click(function() { AuthorizeMachine(msg); return false; })
					.text('Authorize'))
				);
			} else {
				row.append($('<td>').text((col in msg) ? msg[col] : ''));
			}
		}
	});
	$('.content table')
		.find('tbody')
		.append(row);
}

function MachineAuthorized(obj) {
	if (registered_systems == undefined)
		return false;
	
	var found = false;
	registered_systems.forEach(function(val) {
		found = (val.name == obj.name && val.public_key == obj.public_key);
	});
	
	return found;
}

$(document).ready(function() {
	var columns = [];
	var thead = $('.content table thead tr th')
		.each(function( index, value )
		{
			var header = $('a', value).length == 1 ? $('a', value).text() : $(value).text();
			header = header.toLowerCase().replace(' ', '_');
			columns.push(header);
		});
	
	var client = mqtt.connect("ws://192.168.56.1:8080/");
	client.subscribe(SubChannelPath("machines"));

	client.on("message", function(topic, payload) {
		if (topic == SubChannelPath("machines"))
			AddMachineEntry(payload, columns);
		else
			log([topic, payload].join(" : "));
		
		client.end();
	});

	client.publish(BaseChannelPath(), "echo");
});
